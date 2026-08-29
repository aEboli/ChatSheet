using System;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Storage;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 「对着一个模型点一次，看它答不答话」。
    ///
    /// 请求形态是真实对话请求的**真子集**：去掉工具、去掉图片、整段不写思考参数、
    /// 输出上限压到最小。只去不换是刻意的——只要有一个字段换成了别的值，
    /// 而那个值恰好会被拒，探测就会两头误判：
    ///
    /// - 换成模型不接受的值 → 恒定判未知，用户点多少次都问不出答案
    /// - 真实对话用的值会被拒、而探测用的值不会 → 探测给绿灯，真实对话每次都失败
    ///
    /// 后者更坏，因为它主动骗人。
    /// </summary>
    internal static class ModelProbe
    {
        /// <summary>
        /// 固定的探测提示。
        ///
        /// 必须带一条 user 消息：Anthropic 与 Gemini 会把 system 抽到顶层字段，
        /// 一个「只带系统提示」的请求在那两个协议上会产出空的 messages/contents，
        /// 被服务端以 400 拒绝——那时错误说的是我们的请求，不是模型。
        ///
        /// 内容短到一个词，问的只是「你在吗」。
        /// </summary>
        private const string Prompt = "hi";

        /// <summary>
        /// 探测的输出上限。
        ///
        /// 压到最小但不压到 1：有的模型对过小的上限直接报 400，那属于我们自己的请求
        /// 有问题，会判未知——等于花了钱没拿到答案。16 足够放一个招呼。
        /// </summary>
        private const int MaxOutputTokens = 16;

        /// <summary>
        /// 探测的截止时间。
        ///
        /// 必须自带一个：HttpClient.Timeout 是 InfiniteTimeSpan，不给截止时间就等于
        /// 没有超时——挂住的网关会把这一行永久停在「正在确认」，还把后面排队的全堵死。
        ///
        /// 刻意不含退避：探测本来就不重试。
        /// </summary>
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

        /// <summary>
        /// 单飞闸门。
        ///
        /// 进行中再点则排队，不并发：并发招限流，而限流判未知——花了钱没拿到答案。
        /// </summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>我方超时的错误码。用户取消不带码，两者必须分得开。</summary>
        internal const string TimeoutCode = "PROBE_TIMEOUT";

        /// <summary>排队中的探测数，供面板显示「前面还有几个」。</summary>
        internal static int Queued => Math.Max(0, _waiting);

        private static int _waiting;

        /// <summary>
        /// 探一个模型。返回三态，不写入任何缓存——记档由调用方做，
        /// 这样「探测」与「记账」可以分别测。
        ///
        /// 用户取消时抛 OperationCanceledException，调用方据此**不记**任何判定：
        /// 取消不是关于模型的事实。
        /// </summary>
        internal static async Task<AvailabilityVerdict> ProbeAsync(
            ResolvedConnection connection,
            string model,
            OutputLimitField? outputLimit,
            CancellationToken cancellationToken)
        {
            if (connection == null || string.IsNullOrWhiteSpace(model))
            {
                return AvailabilityVerdict.Unknown;
            }

            Interlocked.Increment(ref _waiting);
            try
            {
                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Decrement(ref _waiting);
                throw;
            }

            Interlocked.Decrement(ref _waiting);

            try
            {
                return await SendAsync(connection, model, outputLimit, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<AvailabilityVerdict> SendAsync(
            ResolvedConnection connection,
            string model,
            OutputLimitField? outputLimit,
            CancellationToken cancellationToken)
        {
            var request = new ChatRequest
            {
                Protocol = connection.Protocol,
                BaseUrl = connection.BaseUrl,
                Token = connection.Token,
                Model = model,
                IncludeTools = false,
                SuppressThinking = true,
                MaxOutputTokens = MaxOutputTokens,
                OutputLimitOverride = outputLimit,
            };

            request.Messages.Add(ChatMessage.FromUser(Prompt));

            // 我方超时与用户取消必须分得开：两者都会让 StreamAsync 抛
            // OperationCanceledException，而前者要判未知、后者一个字都不该记。
            using (var deadline = new CancellationTokenSource(Deadline))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, deadline.Token))
            {
                var reached = false;

                try
                {
                    using (var client = new ChatClient())
                    {
                        await client.StreamAsync(
                            request,
                            chatEvent =>
                            {
                                // 收到任何事件就是到达了模型。
                                reached = true;
                                return Task.CompletedTask;
                            },
                            linked.Token,
                            onRetry: null,
                            // 不走退避：TotalBackoff 是 23 秒，与「点一下就知道」矛盾。
                            maxRetries: 0).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 用户主动取消。原样上抛，调用方不记判定。
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // 我方截止时间到。包装成带码的异常，好让判据把它归成未知。
                    throw new ProviderException(
                        TimeoutCode,
                        $"确认 {model} 超过 {Deadline.TotalSeconds:0} 秒未收到回复。");
                }
                catch (ProviderException ex)
                {
                    return ModelAvailability.Classify(ex, model);
                }

                // 200 但一个事件都没收到：不判可用。到达了服务不等于到达了模型——
                // 网关以 200 开流再什么都不给，正是别名模型的典型表现。
                return reached ? AvailabilityVerdict.Available : AvailabilityVerdict.Unknown;
            }
        }

        /// <summary>仅供测试重置闸门与计数。</summary>
        internal static void ResetForTest()
        {
            _waiting = 0;
            while (Gate.CurrentCount == 0)
            {
                Gate.Release();
            }
        }
    }
}
