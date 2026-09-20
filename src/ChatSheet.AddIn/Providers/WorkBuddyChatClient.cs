using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// WorkBuddy ACP 的一次会话客户端。
    ///
    /// ACP 的 prompt 是连续会话，不是 Chat Completions 那种每次携带完整 messages；
    /// 首次请求把 ChatSheet 的系统提示和历史压成一个 prompt，后续只回灌新产生的
    /// 工具结果或续跑提示。WorkBuddy 自己的工具保持关闭，表格工具仍由 AgentRunner
    /// 的文本协议解析并经过现有审批链路执行。
    /// </summary>
    internal sealed class WorkBuddyChatClient : IChatStreamClient
    {
        private const string UnavailableCode = "WORKBUDDY_UNAVAILABLE";
        private const string NotAuthorizedCode = "WORKBUDDY_NOT_AUTHORIZED";
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly WorkBuddyAcpPaths _paths;
        private readonly HashSet<ChatMessage> _sentMessages = new HashSet<ChatMessage>();
        private WorkBuddyProcess _process;
        private ConcurrentQueue<string> _output;
        private SemaphoreSlim _outputReady;
        private string _sessionId;
        private string _systemPrompt;
        private int _nextRequestId;
        private bool _disposed;

        internal WorkBuddyChatClient(WorkBuddyAcpPaths paths)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        }

        public async Task StreamAsync(
            ChatRequest request,
            Func<ChatEvent, Task> onEvent,
            CancellationToken cancellationToken,
            Func<int, TimeSpan, string, Task> onRetry = null,
            int? maxRetries = null)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (onEvent == null)
            {
                throw new ArgumentNullException(nameof(onEvent));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) { throw new ObjectDisposedException(nameof(WorkBuddyChatClient)); }

            using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
            {
                try
                {
                    await StreamCoreAsync(request, onEvent, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ProviderException("WORKBUDDY_TIMEOUT", "WorkBuddy 回复超时，请重试或切换模型。");
                }
            }
        }

        private async Task StreamCoreAsync(
            ChatRequest request,
            Func<ChatEvent, Task> onEvent,
            CancellationToken cancellationToken)
        {
            try
            {
                await EnsureSessionAsync(request.Model, cancellationToken).ConfigureAwait(false);

                var messages = request.Messages ?? new List<ChatMessage>();
                var prompt = BuildPrompt(messages);
                if (prompt.Count == 0)
                {
                    prompt.Add(new JObject
                    {
                        ["type"] = "text",
                        ["text"] = "请继续处理当前任务。",
                    });
                }

                var result = await SendRequestAsync(
                    "session/prompt",
                    new JObject
                    {
                        ["sessionId"] = _sessionId,
                        ["prompt"] = prompt,
                    },
                    message => HandleNotificationAsync(message, onEvent),
                    cancellationToken).ConfigureAwait(false);

                foreach (var message in messages)
                {
                    _sentMessages.Add(message);
                }

                await onEvent(new ChatEvent
                {
                    Kind = ChatEventKind.Completed,
                    FinishReason = result?.Value<string>("stopReason"),
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryCancelSessionAsync().ConfigureAwait(false);
                StopProcess();
                throw;
            }
            catch
            {
                StopProcess();
                throw;
            }
        }

        private async Task EnsureSessionAsync(string model, CancellationToken cancellationToken)
        {
            if (_process != null && !_process.HasExited && !string.IsNullOrWhiteSpace(_sessionId))
            {
                return;
            }

            if (_process != null)
            {
                StopProcess();
            }

            _process = StartProcess();
            Log.Info("WorkBuddy ACP 对话进程已启动");

            await SendRequestAsync(
                "initialize",
                new JObject
                {
                    ["protocolVersion"] = 1,
                    ["clientCapabilities"] = new JObject(),
                    ["clientInfo"] = new JObject
                    {
                        ["name"] = "ChatSheet",
                        ["version"] = typeof(WorkBuddyChatClient).Assembly.GetName().Version.ToString(),
                    },
                },
                null,
                cancellationToken).ConfigureAwait(false);
            Log.Info("WorkBuddy ACP 对话 initialize 已完成");

            var session = await SendRequestAsync(
                "session/new",
                new JObject
                {
                    ["cwd"] = Environment.CurrentDirectory,
                    ["mcpServers"] = new JArray(),
                },
                null,
                cancellationToken).ConfigureAwait(false);

            _sessionId = session?.Value<string>("sessionId");
            if (string.IsNullOrWhiteSpace(_sessionId))
            {
                throw new ProviderException(UnavailableCode, "WorkBuddy ACP 未返回有效会话。" );
            }
            Log.Info("WorkBuddy ACP 对话 session/new 已完成");

            if (!string.IsNullOrWhiteSpace(model))
            {
                await SendRequestAsync(
                    "session/set_model",
                    new JObject
                    {
                        ["sessionId"] = _sessionId,
                        ["modelId"] = model,
                    },
                    null,
                    cancellationToken).ConfigureAwait(false);
                Log.Info("WorkBuddy ACP 对话模型已设置");
            }
        }

        private JArray BuildPrompt(IReadOnlyList<ChatMessage> messages)
        {
            var prompt = new JArray();
            var pending = messages.Where(message => !_sentMessages.Contains(message)).ToList();
            var system = messages.FirstOrDefault(message => message.Role == ChatRole.System);

            if (_sentMessages.Count == 0)
            {
                foreach (var message in messages)
                {
                    AppendMessage(prompt, message, includeAssistant: true, includeSystem: true);
                }

                _systemPrompt = system?.Content;

                return prompt;
            }

            if (system != null && system.Content != _systemPrompt)
            {
                AppendMessage(prompt, system, includeAssistant: false, includeSystem: true);
                _systemPrompt = system.Content;
            }

            // ACP 已经保存了上一条 assistant 输出。只把新用户消息和工具结果回灌，
            // 避免把模型自己的上一条回答再伪装成用户消息发送一次。
            foreach (var message in pending)
            {
                AppendMessage(prompt, message, includeAssistant: false, includeSystem: false);
            }

            return prompt;
        }

        private static void AppendMessage(
            JArray prompt,
            ChatMessage message,
            bool includeAssistant,
            bool includeSystem)
        {
            if (message == null)
            {
                return;
            }

            if (message.Role == ChatRole.System && !includeSystem)
            {
                return;
            }

            if (message.Role == ChatRole.Assistant && !includeAssistant)
            {
                return;
            }

            var label = message.Role switch
            {
                ChatRole.System => "[ChatSheet 系统指令]",
                ChatRole.Assistant => "[助手历史]",
                ChatRole.Tool => "[ChatSheet 工具执行结果]",
                _ => message.IsTextProtocolToolResult
                    ? "[ChatSheet 工具执行结果]"
                    : "[用户消息]",
            };

            var text = (message.Content ?? string.Empty).Trim();
            if (message.ToolCalls.Count > 0)
            {
                var calls = string.Join(
                    "\n",
                    message.ToolCalls.Select(call =>
                        $"工具调用：{call.Name}\n参数：{call.ArgumentsJson}"));
                text = text.Length == 0 ? calls : text + "\n" + calls;
            }

            prompt.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = label + "\n" + text,
            });

            foreach (var image in message.Images)
            {
                if (image == null || string.IsNullOrWhiteSpace(image.Base64))
                {
                    continue;
                }

                prompt.Add(new JObject
                {
                    ["type"] = "image",
                    ["data"] = image.Base64,
                    ["mimeType"] = image.MediaType,
                });
            }
        }

        private async Task<JObject> SendRequestAsync(
            string method,
            JObject parameters,
            Func<JObject, Task> onNotification,
            CancellationToken cancellationToken)
        {
            EnsureProcess();

            var id = ++_nextRequestId;
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JObject(),
            };
            Log.Info($"WorkBuddy ACP 请求：{method}（id={id}）");

            try
            {
                await WriteUtf8Async(
                    _process.Input,
                    request.ToString(Formatting.None) + Environment.NewLine).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new ProviderException(UnavailableCode, "无法向 WorkBuddy ACP 发送请求。", ex);
            }

            while (true)
            {
                await _outputReady.WaitAsync(cancellationToken).ConfigureAwait(false);
                _output.TryDequeue(out var line);
                if (line == null)
                {
                    throw new ProviderException(UnavailableCode, "WorkBuddy ACP 进程已退出。" );
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                line = line.TrimStart('\uFEFF');

                JObject message;
                try
                {
                    message = JObject.Parse(line);
                }
                catch (JsonException)
                {
                    // CLI 的非协议输出不能成为面板或日志中的信息来源。
                    continue;
                }

                if (message["id"] == null)
                {
                    if (onNotification != null)
                    {
                        await onNotification(message).ConfigureAwait(false);
                    }

                    continue;
                }

                if (message["method"] != null)
                {
                    var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"].DeepClone() };
                    if (message.Value<string>("method") == "session/request_permission")
                    {
                        response["result"] = new JObject
                        {
                            ["outcome"] = new JObject { ["outcome"] = "cancelled" },
                        };
                    }
                    else
                    {
                        response["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not found" };
                    }
                    await WriteUtf8Async(_process.Input,
                        response.ToString(Formatting.None) + Environment.NewLine).ConfigureAwait(false);
                    continue;
                }

                if (!JToken.DeepEquals(message["id"], new JValue(id)))
                {
                    continue;
                }

                var error = message["error"] as JObject;
                if (error != null)
                {
                    throw MapError(error);
                }

                Log.Info($"WorkBuddy ACP 响应完成：{method}（id={id}）");
                return message["result"] as JObject
                    ?? throw new ProviderException(UnavailableCode, "WorkBuddy ACP 返回了无效结果。" );
            }
        }

        private static async Task HandleNotificationAsync(
            JObject message,
            Func<ChatEvent, Task> onEvent)
        {
            if (!string.Equals(message.Value<string>("method"), "session/update", StringComparison.Ordinal))
            {
                return;
            }

            var update = message.SelectToken("params.update") as JObject;
            var kind = update?.Value<string>("sessionUpdate");
            if (update == null || string.IsNullOrEmpty(kind))
            {
                return;
            }

            if (kind == "agent_message_chunk" || kind == "agent_thought_chunk")
            {
                foreach (var text in ContentTexts(update["content"]))
                {
                    await onEvent(new ChatEvent
                    {
                        Kind = kind == "agent_thought_chunk"
                            ? ChatEventKind.ThinkingDelta
                            : ChatEventKind.TextDelta,
                        Text = text,
                    }).ConfigureAwait(false);
                }

                return;
            }

            if (kind == "usage_update")
            {
                var promptTokens = ReadTokenCount(update, "promptTokens", "prompt_tokens", "inputTokens", "input_tokens", "input");
                var completionTokens = ReadTokenCount(update, "completionTokens", "completion_tokens", "outputTokens", "output_tokens", "output");
                if (promptTokens > 0 || completionTokens > 0)
                {
                    await onEvent(new ChatEvent
                    {
                        Kind = ChatEventKind.Usage,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                    }).ConfigureAwait(false);
                }
            }
        }

        private static IEnumerable<string> ContentTexts(JToken content)
        {
            if (content is JObject item)
            {
                var text = item.Value<string>("text");
                if (!string.IsNullOrEmpty(text))
                {
                    yield return text;
                }

                yield break;
            }

            if (content is JArray array)
            {
                foreach (var child in array)
                {
                    foreach (var text in ContentTexts(child))
                    {
                        yield return text;
                    }
                }
            }
        }

        private static int ReadTokenCount(JObject update, params string[] names)
        {
            foreach (var property in update.Descendants().OfType<JProperty>())
            {
                if (!names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (property.Value.Type == JTokenType.Integer)
                {
                    return Math.Max(0, property.Value.Value<int>());
                }
            }

            return 0;
        }

        private static ProviderException MapError(JObject error)
        {
            var code = error["code"]?.ToString() ?? string.Empty;
            var text = error.Value<string>("message") ?? string.Empty;
            var marker = (code + " " + text + " " + error["data"]?.ToString(Formatting.None)).ToLowerInvariant();

            if (code == "-32000" || IsAuthMarker(marker))
            {
                return new ProviderException(NotAuthorizedCode, "WorkBuddy 当前未授权。" );
            }

            return new ProviderException("WORKBUDDY_ACP_ERROR", "WorkBuddy ACP 返回了错误。" );
        }

        private static bool IsAuthMarker(string text)
        {
            return text.IndexOf("auth_required", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("authentication required", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("not authenticated", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("unauthorized", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("not logged in", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("login required", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("please login", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("未授权", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("未登录", StringComparison.Ordinal) >= 0;
        }

        private WorkBuddyProcess StartProcess()
        {
            var output = new ConcurrentQueue<string>();
            var outputReady = new SemaphoreSlim(0);
            _output = output;
            _outputReady = outputReady;
            try
            {
                var process = WorkBuddyProcess.Start(_paths, Path.GetDirectoryName(_paths.CliPath));
                _ = PumpOutputAsync(process, output, outputReady);
                return process;
            }
            catch (Exception ex)
            {
                throw new ProviderException(UnavailableCode, "无法启动 WorkBuddy，请确认安装完整后重试。", ex);
            }
        }

        private static async Task PumpOutputAsync(
            WorkBuddyProcess process,
            ConcurrentQueue<string> output,
            SemaphoreSlim outputReady)
        {
            try
            {
                string line;
                while ((line = await process.Output.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    output.Enqueue(line);
                    outputReady.Release();
                }
            }
            catch
            {
            }
            finally
            {
                output.Enqueue(null);
                outputReady.Release();
            }
        }

        private void EnsureProcess()
        {
            if (_process == null || _process.HasExited)
            {
                throw new ProviderException(UnavailableCode, "WorkBuddy ACP 进程不可用。" );
            }
        }

        private async Task TryCancelSessionAsync()
        {
            if (_process == null || _process.HasExited || string.IsNullOrWhiteSpace(_sessionId))
            {
                return;
            }

            try
            {
                var notification = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "session/cancel",
                    ["params"] = new JObject { ["sessionId"] = _sessionId },
                };
                await WriteUtf8Async(
                    _process.Input,
                    notification.ToString(Formatting.None) + Environment.NewLine).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private void StopProcess()
        {
            var process = _process;
            _process = null;
            _sessionId = null;
            _systemPrompt = null;
            _sentMessages.Clear();

            if (process == null)
            {
                return;
            }

            try { process.Dispose(); } catch { }
        }

        private static async Task WriteUtf8Async(Stream stream, string text)
        {
            var bytes = Utf8NoBom.GetBytes(text ?? string.Empty);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopProcess();
        }

    }
}
