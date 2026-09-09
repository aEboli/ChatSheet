using System;
using System.Collections.Generic;
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

        /// <summary>
        /// 闸门剩余许可数，供测试断言「批跑期间闸门确实被占着」。
        /// 不给生产代码用——它只是一个瞬时值，读到之后立刻可能变。
        /// </summary>
        internal static int GatePermits => Gate.CurrentCount;

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

        /// <summary>
        /// 批量探测。整批只占一次单飞闸门，批内按 concurrency 并发。
        ///
        /// 为什么这样分层，而不是把闸门本身放宽到 5：闸门放宽等于**所有**探测都能并发，
        /// 包括用户零散点的「试一下」——那些是随手点的，谁也说不清同时会有几个在飞。
        /// 整批占一次闸门则保证：批跑的时候零散探测排队等着，批内的并发数是确定的 5。
        ///
        /// 并发确实会招限流，而限流判「未确认」——花了钱没拿到答案。调用方（面板）
        /// 因此要把这个代价写在按钮上，让用户点之前就知道。
        ///
        /// onResult 每探完一个就回调一次（模型名、判定、已完成数）。回调在探测线程上跑，
        /// 调用方自己负责线程安全——这里不加锁，因为调用方只是推进度。
        ///
        /// onStart 在某个模型真的开始探时回调一次（拿到并发槽位之后，发请求之前）。
        /// 可为 null。存在的理由：只有 onResult 的话，调用方能说出「已经探完几个」，
        /// 说不出「此刻在飞的是哪几个」——并发 5 时那是五个模型，而面板要把它们标出来。
        /// 时机必须在拿到槽位之后：排在后面等槽位的模型还没开始发请求，
        /// 提前标上等于说了假话。
        ///
        /// stopAfterAvailable 是「探出这么多个可用的就别再发了」，0 表示全部探完。
        /// 三件事必须一起成立：
        ///
        ///   · 只数本批产出的 Available。限流与我方截止时间都判 Unknown，那是「花了钱
        ///     没拿到答案」，算进目标等于在最坏结果上宣布成功。也不看
        ///     ModelAvailability 里的历史判定——本方法刻意不碰记档，「哪些值得先探」
        ///     由调用方通过 models 的顺序表达。
        ///   · 达标只**停止派发**，不取消。已经发出去的（最多 concurrency-1 条）一律
        ///     等它跑完并照常回调 onResult：钱已经付了。取消会同时丢掉那几条的判定
        ///     （SendAsync 在 token 已取消时原样上抛 OCE，而下面的任务体只 catch
        ///     ProviderException）、在请求仍在飞时放开单飞闸门、并留下访问已释放信号量
        ///     的孤儿任务。
        ///   · 达标不抛异常。于是 OperationCanceledException 仍然专属用户取消，
        ///     调用方分辨两种结局不必靠影子标志。
        /// </summary>
        internal static async Task<ProbeSweepOutcome> ProbeManyAsync(
            ResolvedConnection connection,
            IReadOnlyList<string> models,
            int concurrency,
            Func<string, OutputLimitField?> outputLimitFor,
            Func<string, AvailabilityVerdict, int, Task> onResult,
            CancellationToken cancellationToken,
            Func<string, Task> onStart = null,
            int stopAfterAvailable = 0)
        {
            if (connection == null || models == null || models.Count == 0)
            {
                return default(ProbeSweepOutcome);
            }

            var width = Math.Max(1, Math.Min(concurrency, 16));

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (var slots = new SemaphoreSlim(width, width))
                {
                    var done = 0;
                    var available = 0;
                    var enough = 0;
                    var dispatched = 0;
                    var tasks = new List<Task>(models.Count);

                    foreach (var model in models)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);

                        // 达标检查必须在拿到槽位之后。放在 WaitAsync 之前会晚一整轮：
                        // 我们可能正是在等槽位期间达标的，而唤醒我们的那次 Release 来自
                        // 某个任务的 finally——它已经把 available 数上去了，可那一轮的
                        // 检查早就过去了，于是照样多派发一条。
                        //
                        // break 前必须归还槽位。忘了不死锁也不报错，只是许可数短一个，
                        // 完全静默。
                        if (Volatile.Read(ref enough) != 0)
                        {
                            slots.Release();
                            break;
                        }

                        dispatched++;

                        var captured = model;
                        tasks.Add(Task.Run(
                            async () =>
                            {
                                try
                                {
                                    // 拿到槽位了，这一个真的开始探。
                                    if (onStart != null)
                                    {
                                        await onStart(captured).ConfigureAwait(false);
                                    }

                                    var verdict = AvailabilityVerdict.Unknown;
                                    try
                                    {
                                        verdict = await SendAsync(
                                            connection,
                                            captured,
                                            outputLimitFor?.Invoke(captured),
                                            cancellationToken).ConfigureAwait(false);
                                    }
                                    catch (ProviderException ex)
                                    {
                                        // 单个失败本身就是一条判定，不该中断整批。
                                        verdict = ModelAvailability.Classify(ex, captured);
                                    }

                                    var completed = Interlocked.Increment(ref done);

                                    // 置位必须在 await onResult 之前，也必须在 finally 里
                                    // 归还槽位之前。onResult 要过 WebView2 桥推一条进度，
                                    // 那一步能耗到毫秒级；置位排在它后面的话，派发循环
                                    // 已经拿到槽位并把下一条发出去了。
                                    if (verdict == AvailabilityVerdict.Available)
                                    {
                                        var found = Interlocked.Increment(ref available);
                                        if (stopAfterAvailable > 0 && found >= stopAfterAvailable)
                                        {
                                            Volatile.Write(ref enough, 1);
                                        }
                                    }

                                    if (onResult != null)
                                    {
                                        await onResult(captured, verdict, completed)
                                            .ConfigureAwait(false);
                                    }
                                }
                                finally
                                {
                                    slots.Release();
                                }
                            },
                            cancellationToken));
                    }

                    // 达标之后仍然等在飞的那几条跑完，再退出。这一步是「不取消」的
                    // 全部实现：闸门到这里才放开，孤儿任务不存在，已付费的判定都记下了。
                    await Task.WhenAll(tasks).ConfigureAwait(false);

                    var found = Volatile.Read(ref available);
                    return new ProbeSweepOutcome(
                        dispatched,
                        Volatile.Read(ref done),
                        found,
                        stopAfterAvailable > 0 && found >= stopAfterAvailable);
                }
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

    /// <summary>
    /// 一次批量探测跑完之后的结局。
    ///
    /// 只在正常返回时才有值——用户取消那条路是抛出而不是返回的，所以调用方**不能**
    /// 靠这个结构体去说「实际发了多少条」：那正是取消路径上唯一需要它的地方，
    /// 而那条路上它拿不到值。给用户看的条数与可用个数由调用方自己在回调里数。
    ///
    /// 这里的字段是给测试用的：Dispatched 记的是派发数（Task.Run 入队数），
    /// 与 Completed 的差额只在取消路径上非零。
    /// </summary>
    internal readonly struct ProbeSweepOutcome
    {
        internal ProbeSweepOutcome(int dispatched, int completed, int availableFound, bool targetMet)
        {
            Dispatched = dispatched;
            Completed = completed;
            AvailableFound = availableFound;
            TargetMet = targetMet;
        }

        /// <summary>派发出去的条数。</summary>
        internal int Dispatched { get; }

        /// <summary>拿到判定并回调过 onResult 的条数。</summary>
        internal int Completed { get; }

        /// <summary>本批判 Available 的条数。</summary>
        internal int AvailableFound { get; }

        /// <summary>是否因为达到目标数而停止派发。</summary>
        internal bool TargetMet { get; }
    }
}
