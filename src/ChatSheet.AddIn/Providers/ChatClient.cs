using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 接口客户端。负责发起流式对话与模型发现。
    ///
    /// 所有网络请求都在加载项进程内发起，密钥不经过面板 UI。
    /// </summary>
    internal sealed class ChatClient : IDisposable
    {
        private readonly HttpClient _http;

        internal ChatClient()
        {
            // 允许较老的网关：部分自建代理只支持 TLS 1.2。
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
            }

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };

            _http = new HttpClient(handler)
            {
                // 流式对话可能持续很久，超时交由取消令牌控制，
                // 这里设为无限，否则长回答会在中途被 HttpClient 掐断。
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        /// <summary>
        /// 发起流式对话，通过回调逐个交付归一化事件。
        ///
        /// 两类自动重试都在这里收口：
        /// 一是 Anthropic 的两代思考参数互不兼容，而请求前只能按模型名启发式判断，
        /// 服务端以 400 拒绝时改用另一种方式重试一次，使用户不必理解代际差异；
        /// 二是网络与网关故障按退避重试若干次（见 <see cref="RetryPolicy"/>）。
        /// </summary>
        /// <param name="onRetry">
        /// 重试前的通知回调，用于把「正在重试」显示给用户。
        /// 参数为已重试次数、等待时长与原因。
        /// </param>
        internal async Task StreamAsync(
            ChatRequest request,
            Func<ChatEvent, Task> onEvent,
            CancellationToken cancellationToken,
            Func<int, TimeSpan, string, Task> onRetry = null)
        {
            try
            {
                await StreamWithRetryAsync(request, onEvent, cancellationToken, onRetry).ConfigureAwait(false);
            }
            catch (ProviderException ex) when (
                request.Protocol == ProtocolKind.AnthropicMessages &&
                request.AnthropicStyleOverride == null &&
                IsThinkingStyleMismatch(ex))
            {
                var current = Thinking.StyleFor(request.Model);
                var fallback = current == AnthropicThinkingStyle.Adaptive
                    ? AnthropicThinkingStyle.Budget
                    : AnthropicThinkingStyle.Adaptive;

                Log.Warn($"思考参数方式不被模型接受（{ex.Message}），改用 {fallback} 方式重试");
                request.AnthropicStyleOverride = fallback;

                await StreamWithRetryAsync(request, onEvent, cancellationToken, onRetry).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 按退避策略重试流式请求。
        ///
        /// 关键约束：只有在本次尝试尚未交付任何事件时才允许重试。
        /// 流式响应是边读边交付的，若已经把半截回答推给界面再重来一次，
        /// 用户会看到同一段话出现两遍，上下文里也会留下拼接错乱的助手消息。
        /// 因此中途断流一律作为失败上报，由用户决定是否重新提问。
        /// </summary>
        private async Task StreamWithRetryAsync(
            ChatRequest request,
            Func<ChatEvent, Task> onEvent,
            CancellationToken cancellationToken,
            Func<int, TimeSpan, string, Task> onRetry)
        {
            for (var retry = 0; ; retry++)
            {
                var delivered = false;

                try
                {
                    await StreamOnceAsync(
                        request,
                        async chatEvent =>
                        {
                            delivered = true;
                            await onEvent(chatEvent).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);

                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 用户主动停止，不属于故障。
                    throw;
                }
                catch (Exception ex) when (
                    retry < RetryPolicy.MaxRetries &&
                    !delivered &&
                    RetryPolicy.IsTransient(ex))
                {
                    var attempt = retry + 1;
                    var delay = RetryPolicy.DelayFor(attempt, (ex as ProviderException)?.RetryAfter);
                    var notice = RetryPolicy.Describe(attempt, delay, ex.Message);

                    Log.Warn(notice);

                    if (onRetry != null)
                    {
                        await onRetry(attempt, delay, ex.Message).ConfigureAwait(false);
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (delivered && RetryPolicy.IsTransient(ex))
                {
                    // 已经交付过内容，重试会导致回答重复，只能如实上报。
                    Log.Warn("流式响应中断且已交付部分内容，不重试：" + ex.Message);
                    throw;
                }
            }
        }

        /// <summary>
        /// 判断错误是否由思考参数的代际不匹配引起。
        /// 服务端的提示文本形如「"thinking.type.enabled" is not supported」。
        /// </summary>
        private static bool IsThinkingStyleMismatch(ProviderException ex)
        {
            if (!ex.Code.StartsWith("HTTP_4", StringComparison.Ordinal))
            {
                return false;
            }

            var message = ex.Message ?? string.Empty;
            return message.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("output_config", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("effort", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("budget_tokens", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task StreamOnceAsync(
            ChatRequest request,
            Func<ChatEvent, Task> onEvent,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                throw new ProviderException("MODEL_REQUIRED", "尚未选择模型。");
            }

            var endpoint = Protocols.BuildChatEndpoint(request.Protocol, request.BaseUrl, request.Model, stream: true);
            var body = RequestBuilder.Build(request, stream: true);

            using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                message.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false), "application/json");
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                ApplyAuth(message, request.Protocol, request.Token);

                HttpResponseMessage response;
                try
                {
                    // 必须用 ResponseHeadersRead：否则 HttpClient 会缓冲整个响应体，
                    // 流式输出就退化成一次性返回，用户看不到逐字生成。
                    response = await _http
                        .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ProviderException("NETWORK_ERROR", DescribeNetworkError(ex, endpoint), ex);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw await BuildHttpErrorAsync(response).ConfigureAwait(false);
                    }

                    var parser = StreamParser.Create(request.Protocol);
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        await SseReader.ReadAsync(
                            stream,
                            async frame =>
                            {
                                foreach (var chatEvent in parser.Parse(frame))
                                {
                                    await onEvent(chatEvent).ConfigureAwait(false);
                                }

                                return true;
                            },
                            cancellationToken).ConfigureAwait(false);
                    }

                    // 流可能在没有显式结束事件时断开，补交累积的工具调用。
                    foreach (var chatEvent in parser.Flush())
                    {
                        await onEvent(chatEvent).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// 获取模型列表。
        /// 不同服务的返回结构不一致，这里做宽松提取，取不到就返回空列表由界面提示手填。
        /// </summary>
        internal async Task<IReadOnlyList<string>> ListModelsAsync(
            ProtocolKind protocol,
            string baseUrl,
            string token,
            CancellationToken cancellationToken,
            Func<int, TimeSpan, string, Task> onRetry = null)
        {
            // 一次性请求，重来不会产生重复内容，因此可以整体重试。
            for (var retry = 0; ; retry++)
            {
                try
                {
                    return await ListModelsOnceAsync(protocol, baseUrl, token, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (retry < RetryPolicy.MaxRetries && RetryPolicy.IsTransient(ex))
                {
                    var attempt = retry + 1;
                    var delay = RetryPolicy.DelayFor(attempt, (ex as ProviderException)?.RetryAfter);

                    Log.Warn("获取模型列表失败：" + RetryPolicy.Describe(attempt, delay, ex.Message));

                    if (onRetry != null)
                    {
                        await onRetry(attempt, delay, ex.Message).ConfigureAwait(false);
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<IReadOnlyList<string>> ListModelsOnceAsync(
            ProtocolKind protocol,
            string baseUrl,
            string token,
            CancellationToken cancellationToken)
        {
            var endpoint = Protocols.BuildModelsEndpoint(protocol, baseUrl);
            if (endpoint == null)
            {
                return Array.Empty<string>();
            }

            using (var message = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                ApplyAuth(message, protocol, token);

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 取消要原样抛出：包成 NETWORK_ERROR 会被重试策略当成可重试故障，
                    // 于是用户已经放弃或已超时的请求还会再试几轮。
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ProviderException("NETWORK_ERROR", DescribeNetworkError(ex, endpoint), ex);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw await BuildHttpErrorAsync(response).ConfigureAwait(false);
                    }

                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ExtractModelIds(text);
                }
            }
        }

        /// <summary>从多种可能的响应结构中提取模型标识。</summary>
        internal static IReadOnlyList<string> ExtractModelIds(string json)
        {
            var ids = new List<string>();
            try
            {
                var root = JToken.Parse(json);

                // OpenAI 与 Anthropic 都用 data 数组；Gemini 用 models。
                var array = root["data"] as JArray ?? root["models"] as JArray ?? root as JArray;
                if (array == null)
                {
                    return ids;
                }

                foreach (var item in array)
                {
                    string id = null;
                    if (item is JObject obj)
                    {
                        id = obj.Value<string>("id") ?? obj.Value<string>("name") ?? obj.Value<string>("model");
                    }
                    else if (item.Type == JTokenType.String)
                    {
                        id = item.Value<string>();
                    }

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    // Gemini 返回形如 models/gemini-2.0-flash，去掉前缀便于展示与使用。
                    const string prefix = "models/";
                    if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        id = id.Substring(prefix.Length);
                    }

                    ids.Add(id);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("解析模型列表失败：" + ex.Message);
            }

            return ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ApplyAuth(HttpRequestMessage message, ProtocolKind protocol, string token)
        {
            foreach (var pair in Protocols.AuthHeaders(protocol, token))
            {
                if (string.Equals(pair.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    var value = pair.Value;
                    const string bearer = "Bearer ";
                    message.Headers.Authorization = value.StartsWith(bearer, StringComparison.Ordinal)
                        ? new AuthenticationHeaderValue("Bearer", value.Substring(bearer.Length))
                        : new AuthenticationHeaderValue("Bearer", value);
                }
                else
                {
                    message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
            }
        }

        /// <summary>
        /// 把 HTTP 错误转成对用户有意义的消息。
        /// 服务端的错误体结构各异，逐层尝试提取真实原因，
        /// 否则用户只会看到「400 Bad Request」这类无用信息。
        /// </summary>
        private static async Task<ProviderException> BuildHttpErrorAsync(HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;
            string detail = null;

            try
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    detail = ExtractErrorMessage(text);
                }
            }
            catch
            {
            }

            var hint = HintFor(status);
            var message = $"接口返回 {status} {response.ReasonPhrase}";
            if (!string.IsNullOrEmpty(detail)) { message += "：" + detail; }
            if (!string.IsNullOrEmpty(hint)) { message += "。" + hint; }

            return new ProviderException("HTTP_" + status, message)
            {
                RetryAfter = ReadRetryAfter(response),
            };
        }

        /// <summary>
        /// 读取 Retry-After。它可以是秒数，也可以是绝对时间，两种都要认。
        /// 取不到就返回空，由本地退避决定等待多久。
        /// </summary>
        private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
        {
            try
            {
                var retryAfter = response.Headers.RetryAfter;
                if (retryAfter == null)
                {
                    return null;
                }

                if (retryAfter.Delta.HasValue)
                {
                    return retryAfter.Delta.Value;
                }

                if (retryAfter.Date.HasValue)
                {
                    var wait = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                    return wait > TimeSpan.Zero ? wait : (TimeSpan?)null;
                }
            }
            catch
            {
                // 头部格式不合规不该影响主流程。
            }

            return null;
        }

        internal static string ExtractErrorMessage(string body)
        {
            try
            {
                var root = JToken.Parse(body);
                var error = root["error"];
                if (error != null)
                {
                    if (error.Type == JTokenType.String) { return error.Value<string>(); }
                    var nested = error["message"]?.Value<string>();
                    if (!string.IsNullOrEmpty(nested)) { return nested; }
                }

                var direct = root["message"]?.Value<string>();
                if (!string.IsNullOrEmpty(direct)) { return direct; }

                var detail = root["detail"];
                if (detail != null && detail.Type == JTokenType.String) { return detail.Value<string>(); }
            }
            catch
            {
                // 非 JSON 错误体（例如网关的 HTML 页面），截断后原样展示。
            }

            var text = body.Trim();
            return text.Length > 300 ? text.Substring(0, 300) + "…" : text;
        }

        private static string HintFor(int status)
        {
            switch (status)
            {
                case 401:
                case 403:
                    return "请检查密钥是否正确、是否已过期";
                case 404:
                    return "请检查接口地址与模型名是否正确";
                case 429:
                    return "请求过于频繁或额度不足，请稍后重试";
                case 500:
                case 502:
                case 503:
                case 504:
                    return "服务端暂时不可用，请稍后重试";
                default:
                    return null;
            }
        }

        private static string DescribeNetworkError(Exception ex, string endpoint)
        {
            var root = ex;
            while (root.InnerException != null) { root = root.InnerException; }

            return $"无法连接 {endpoint}：{root.Message}";
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
