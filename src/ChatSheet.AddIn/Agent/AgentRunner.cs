using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Hosts;
using ChatSheet.AddIn.Providers;
using ChatSheet.AddIn.Storage;
using ChatSheet.AddIn.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Agent
{
    /// <summary>Agent 向面板推送的进度事件。</summary>
    internal sealed class AgentUpdate
    {
        internal string Kind { get; set; }

        internal string Text { get; set; }

        internal object Payload { get; set; }
    }

    /// <summary>
    /// 审批卡片要展示的影响范围。
    ///
    /// 探到具体范围时给出结构化字段，由面板负责组装文案——地址的中文说明
    /// （「2-10 行 × B-D 列」）在面板侧已有一份实现，这里再拼一遍字符串
    /// 就会出现两套措辞。拿不到范围时才退回 Text。
    /// </summary>
    internal sealed class ImpactEstimate
    {
        /// <summary>没有范围可报时的兜底说明，可为空。</summary>
        internal string Text { get; set; } = string.Empty;

        internal string SheetName { get; set; }

        /// <summary>宿主解析后的 A1 地址；为空表示没探到范围。</summary>
        internal string Address { get; set; }

        internal int? CellCount { get; set; }
    }

    /// <summary>审批请求的结果。</summary>
    internal sealed class ApprovalDecision
    {
        internal bool Approved { get; set; }

        internal string Reason { get; set; }

        /// <summary>本轮后续同类操作是否一并批准。</summary>
        internal bool ApproveRest { get; set; }
    }

    /// <summary>
    /// Agent 主循环：请求模型 → 收集工具调用 → 审批 → 执行 → 回灌结果 → 继续，
    /// 直到模型不再请求工具或达到步数上限。
    /// </summary>
    internal sealed class AgentRunner
    {
        private readonly Func<object> _applicationAccessor;
        private readonly ToolExecutor _tools;
        private readonly WorkbookContext _context;
        private readonly Conversation _conversation = new Conversation();

        /// <summary>
        /// 把委托切到 UI 线程执行。
        ///
        /// 宿主的 COM 对象是 STA 绑定的，而 Agent 循环在 await 之后运行于线程池线程。
        /// 跨单元调用 Excel 会不稳定，可能抛 RPC_E_SERVERCALL_RETRYLATER，
        /// 也可能在宿主繁忙时死锁。因此所有触碰工作簿的操作都必须回到 UI 线程。
        ///
        /// 注意工具层的单元测试跑在 [STAThread] 的 Main 上，天然是 STA，
        /// 因此掩盖了这个问题——真实加载项里必须显式切换。
        /// </summary>
        private readonly Func<Func<object>, Task<object>> _uiInvoker;

        private bool _approveRestOfTurn;

        internal AgentRunner(Func<object> applicationAccessor, Func<Func<object>, Task<object>> uiInvoker = null)
        {
            _applicationAccessor = applicationAccessor;
            _tools = new ToolExecutor(applicationAccessor);
            _context = new WorkbookContext(applicationAccessor);
            // 未提供切换器时原地执行，便于在 STA 测试宿主中直接使用。
            _uiInvoker = uiInvoker ?? (work => Task.FromResult(work()));
        }

        /// <summary>在 UI 线程上执行工具，避免跨 COM 单元调用宿主。</summary>
        private async Task<ToolResult> ExecuteOnUiAsync(string name, JObject args, string undoId = null)
        {
            var result = await _uiInvoker(() => _tools.Execute(name, args, undoId)).ConfigureAwait(false);
            return (ToolResult)result;
        }

        internal ToolExecutor Tools => _tools;

        internal Conversation Conversation => _conversation;

        internal void Reset()
        {
            _conversation.Clear();
        }

        /// <summary>
        /// 执行一轮用户请求。
        /// </summary>
        /// <param name="userInput">用户输入。</param>
        /// <param name="settings">当前设置。</param>
        /// <param name="onUpdate">进度回调，用于把流式内容推给面板。</param>
        /// <param name="requestApproval">审批回调，返回用户决定。</param>
        internal async Task RunAsync(
            string userInput,
            Settings settings,
            Func<AgentUpdate, Task> onUpdate,
            Func<ToolDefinition, JObject, ImpactEstimate, Task<ApprovalDecision>> requestApproval,
            CancellationToken cancellationToken,
            IReadOnlyList<ImageAttachment> images = null)
        {
            // 只带图片不写文字也应允许：贴一张截图问「这个怎么填」是常见用法。
            if (string.IsNullOrWhiteSpace(userInput) && (images == null || images.Count == 0))
            {
                throw new ProviderException("EMPTY_INPUT", "请输入内容或附加图片。");
            }

            var connection = settings.ResolveConnection();
            if (string.IsNullOrWhiteSpace(connection.Model))
            {
                throw new ProviderException("MODEL_REQUIRED", "尚未选择模型，请到设置页选择。");
            }

            _approveRestOfTurn = settings.Approval == ApprovalPolicy.Automatic;

            // 每轮刷新系统提示：工作簿可能已被用户手动改动。
            RefreshSystemPrompt(settings);
            _conversation.Add(ChatMessage.FromUser(userInput ?? string.Empty, images));

            if (images != null && images.Count > 0)
            {
                Log.Info($"本轮附带 {ImageSupport.Describe(images)}");
            }

            using (var client = new ChatClient())
            {
                for (var step = 0; step < settings.MaxSteps; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 先推送压缩前的占用，再压缩。
                    //
                    // 顺序很关键：若先压缩再推送，界面永远只看到压缩后的低占比，
                    // 用户无从知道曾经触达阈值，圆环也不会有任何提示——
                    // 「到达 90% 时提示是否压缩」这件事就完全不可见了。
                    await PushContextAsync(onUpdate, settings).ConfigureAwait(false);

                    var trim = _conversation.TrimToBudget(settings.ContextBudgetTokens);
                    if (trim.Trimmed)
                    {
                        Log.Info($"上下文压缩：{trim.TokensBefore} → {trim.TokensAfter} tokens" +
                            $"（阈值 {trim.TriggerTokens}，预算 {trim.BudgetTokens}），" +
                            $"压缩 {trim.CompressedToolResults} 条工具结果、移除 {trim.DroppedMessages} 条早期记录");

                        await onUpdate(new AgentUpdate
                        {
                            Kind = "context-trimmed",
                            Payload = new
                            {
                                budget = trim.BudgetTokens,
                                trigger = trim.TriggerTokens,
                                before = trim.TokensBefore,
                                after = trim.TokensAfter,
                                compressed = trim.CompressedToolResults,
                                dropped = trim.DroppedMessages,
                            },
                        }).ConfigureAwait(false);

                        // 压缩后再推一次，让圆环回落到实际值。
                        await PushContextAsync(onUpdate, settings).ConfigureAwait(false);
                    }

                    var request = BuildRequest(connection, settings);
                    var assistantText = new System.Text.StringBuilder();
                    var pendingCalls = new List<ToolCall>();
                    var finishReason = (string)null;

                    await client.StreamAsync(
                        request,
                        async chatEvent =>
                        {
                            switch (chatEvent.Kind)
                            {
                                case ChatEventKind.TextDelta:
                                    assistantText.Append(chatEvent.Text);
                                    await onUpdate(new AgentUpdate { Kind = "text", Text = chatEvent.Text }).ConfigureAwait(false);
                                    break;

                                case ChatEventKind.ThinkingDelta:
                                    await onUpdate(new AgentUpdate { Kind = "thinking", Text = chatEvent.Text }).ConfigureAwait(false);
                                    break;

                                case ChatEventKind.ToolCall:
                                    pendingCalls.Add(chatEvent.Call);
                                    break;

                                case ChatEventKind.Usage:
                                    _conversation.RecordUsage(chatEvent.PromptTokens, chatEvent.CompletionTokens);
                                    await onUpdate(new AgentUpdate
                                    {
                                        Kind = "usage",
                                        Payload = new
                                        {
                                            promptTokens = chatEvent.PromptTokens,
                                            completionTokens = chatEvent.CompletionTokens,
                                            estimatedContext = _conversation.EstimateTotalTokens(),
                                            budget = settings.ContextBudgetTokens,
                                        },
                                    }).ConfigureAwait(false);
                                    break;

                                case ChatEventKind.Completed:
                                    finishReason = chatEvent.FinishReason;
                                    break;
                            }
                        },
                        cancellationToken,
                        // 重试期间界面必须有说明：否则退避等待期表现为「发出去了但毫无反应」，
                        // 用户往往会以为卡死而反复点发送。
                        (attempt, delay, reason) => onUpdate(new AgentUpdate
                        {
                            Kind = "retry",
                            Text = RetryPolicy.Describe(attempt, delay, reason),
                            Payload = new
                            {
                                attempt,
                                maxRetries = RetryPolicy.MaxRetries,
                                delaySeconds = (int)Math.Round(delay.TotalSeconds),
                                reason,
                            },
                        })).ConfigureAwait(false);

                    // 记录助手消息，含工具调用，供下一轮作为历史发送。
                    var assistantMessage = ChatMessage.FromAssistant(assistantText.ToString());
                    assistantMessage.ToolCalls.AddRange(pendingCalls);
                    _conversation.Add(assistantMessage);

                    if (pendingCalls.Count == 0)
                    {
                        Log.Info($"对话结束：步数={step + 1} 结束原因={finishReason ?? "<未提供>"} " +
                            $"回复长度={assistantText.Length} 用量={_conversation.LastPromptTokens}入/{_conversation.LastCompletionTokens}出");
                        await onUpdate(new AgentUpdate { Kind = "turn-complete", Payload = new { finishReason } })
                            .ConfigureAwait(false);
                        return;
                    }

                    Log.Info($"第 {step + 1} 步：模型请求 {pendingCalls.Count} 个工具调用（" +
                        string.Join("、", pendingCalls.ConvertAll(c => c.Name)) + "）");

                    foreach (var call in pendingCalls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ExecuteOneAsync(call, settings, onUpdate, requestApproval).ConfigureAwait(false);
                    }
                }

                // 达到步数上限：明确告知而不是静默停止。
                await onUpdate(new AgentUpdate
                {
                    Kind = "step-limit",
                    Text = $"已达到单轮步数上限（{settings.MaxSteps} 步）。任务可能尚未完成，可继续输入以让我接着处理，或到设置页提高上限。",
                }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 推送上下文占用情况，供界面绘制进度圆环。
        /// nearLimit 由后端判定，界面不重复计算阈值，避免两处规则不一致。
        /// </summary>
        private Task PushContextAsync(Func<AgentUpdate, Task> onUpdate, Settings settings)
        {
            var used = _conversation.EstimateTotalTokens();
            var budget = Math.Max(1, settings.ContextBudgetTokens);
            var ratio = Math.Min(1.0, (double)used / budget);

            return onUpdate(new AgentUpdate
            {
                Kind = "context",
                Payload = new
                {
                    used,
                    budget,
                    ratio,
                    percent = (int)Math.Round(ratio * 100),
                    threshold = (int)(Conversation.CompressionThreshold * 100),
                    nearLimit = ratio >= Conversation.CompressionThreshold,
                    lastPromptTokens = _conversation.LastPromptTokens,
                    lastCompletionTokens = _conversation.LastCompletionTokens,
                },
            });
        }

        private async Task ExecuteOneAsync(
            ToolCall call,
            Settings settings,
            Func<AgentUpdate, Task> onUpdate,
            Func<ToolDefinition, JObject, ImpactEstimate, Task<ApprovalDecision>> requestApproval)
        {
            var definition = ToolCatalog.Find(call.Name);
            if (definition == null)
            {
                await FeedToolResultAsync(call, ToolResult.Failure("UNKNOWN_TOOL", $"未知工具：{call.Name}。"), onUpdate)
                    .ConfigureAwait(false);
                return;
            }

            JObject args;
            try
            {
                args = string.IsNullOrWhiteSpace(call.ArgumentsJson)
                    ? new JObject()
                    : JObject.Parse(call.ArgumentsJson);
            }
            catch (Exception ex)
            {
                // 参数 JSON 不完整时给出明确原因，让模型重发而不是崩在这里。
                await FeedToolResultAsync(
                    call,
                    ToolResult.Failure("ARGS_INVALID", $"参数不是合法 JSON：{ex.Message}"),
                    onUpdate).ConfigureAwait(false);
                return;
            }

            await onUpdate(new AgentUpdate
            {
                Kind = "tool-start",
                Payload = new
                {
                    id = call.Id,
                    name = call.Name,
                    risk = definition.Risk.ToString(),
                    args,
                },
            }).ConfigureAwait(false);

            if (definition.RequiresApproval && !_approveRestOfTurn)
            {
                // 审批前先算出影响范围，让用户知道自己在批准什么。
                var impact = await DescribeImpactAsync(definition, args).ConfigureAwait(false);
                var decision = await requestApproval(definition, args, impact).ConfigureAwait(false);

                if (decision.ApproveRest)
                {
                    _approveRestOfTurn = true;
                }

                if (!decision.Approved)
                {
                    var reason = string.IsNullOrWhiteSpace(decision.Reason)
                        ? "用户拒绝了此操作。"
                        : $"用户拒绝了此操作：{decision.Reason}";

                    await FeedToolResultAsync(call, ToolResult.Failure("USER_REJECTED", reason), onUpdate)
                        .ConfigureAwait(false);
                    return;
                }
            }

            // 用工具调用标识作为撤销标识：面板拿到的 tool-result 里带同一个 id，
            // 因此能直接把撤销按钮挂到对应的操作卡片上。
            var result = await ExecuteOnUiAsync(call.Name, args, call.Id).ConfigureAwait(false);
            await FeedToolResultAsync(call, result, onUpdate).ConfigureAwait(false);
        }

        /// <summary>
        /// 估算操作影响，用于审批卡片展示。
        /// 失败不阻塞审批：拿不到精确范围时给出可用的近似描述。
        /// </summary>
        private async Task<ImpactEstimate> DescribeImpactAsync(ToolDefinition definition, JObject args)
        {
            try
            {
                var range = args.Value<string>("range");
                if (string.IsNullOrWhiteSpace(range))
                {
                    return new ImpactEstimate
                    {
                        Text = definition.Risk == ToolRisk.Structure ? "将改变工作簿结构" : string.Empty,
                    };
                }

                var sheet = args.Value<string>("sheet");
                var probe = await ExecuteOnUiAsync("read_range", new JObject
                {
                    ["range"] = range,
                    ["sheet"] = sheet,
                }).ConfigureAwait(false);

                // 大范围会触发读取上限，此时退回用模型给的地址描述。
                // 它未经宿主规范化、也数不出单元格数，但仍能被面板译成行列说明。
                if (!probe.Ok)
                {
                    return new ImpactEstimate { SheetName = sheet, Address = range };
                }

                var payload = JObject.FromObject(probe.Data);
                var rows = payload.Value<int?>("rows") ?? 0;
                var columns = payload.Value<int?>("columns") ?? 0;

                return new ImpactEstimate
                {
                    SheetName = payload.Value<string>("sheet") ?? sheet,
                    Address = payload.Value<string>("address") ?? range,
                    CellCount = rows * columns,
                };
            }
            catch (Exception ex)
            {
                Log.Warn("估算影响范围失败：" + ex.Message);
                return new ImpactEstimate();
            }
        }

        private async Task FeedToolResultAsync(ToolCall call, ToolResult result, Func<AgentUpdate, Task> onUpdate)
        {
            var json = JsonConvert.SerializeObject(result.ToPayload(), Formatting.None);
            _conversation.Add(ChatMessage.FromToolResult(call.Id, call.Name, json));

            Log.Info(result.Ok
                ? $"工具 {call.Name} 执行成功"
                : $"工具 {call.Name} 执行失败：{result.ErrorCode} {result.Error}");

            // 告知面板这条操作能否撤销，以便决定是否显示撤销按钮。
            var undoRecord = _tools.Undo.Find(call.Id);

            await onUpdate(new AgentUpdate
            {
                Kind = "tool-result",
                Payload = new
                {
                    id = call.Id,
                    name = call.Name,
                    ok = result.Ok,
                    error = result.Error,
                    errorCode = result.ErrorCode,
                    data = result.Data,
                    canUndo = undoRecord?.CanUndo ?? false,
                    undoSummary = undoRecord?.Summary,
                },
            }).ConfigureAwait(false);
        }

        private void RefreshSystemPrompt(Settings settings)
        {
            WorkbookSummary summary = null;
            SelectionInfo selection = null;

            try
            {
                summary = _context.GetSummary();
                if (settings.AutoIncludeSelection)
                {
                    selection = _context.GetSelection();
                }
            }
            catch (Exception ex)
            {
                // 取不到工作簿信息时仍要能对话，系统提示里会写明这一点。
                Log.Warn("刷新工作簿上下文失败：" + ex.Message);
            }

            _conversation.SetSystemPrompt(
                SystemPrompt.Build(summary, selection, settings.AutoIncludeSelection));
        }

        private ChatRequest BuildRequest(ResolvedConnection connection, Settings settings)
        {
            var request = new ChatRequest
            {
                Protocol = connection.Protocol,
                BaseUrl = connection.BaseUrl,
                Token = connection.Token,
                Model = connection.Model,
                Thinking = settings.Thinking,
                Temperature = settings.Temperature,
                MaxOutputTokens = settings.MaxOutputTokens,
                IncludeTools = true,
            };

            request.Messages.AddRange(_conversation.Messages);
            return request;
        }
    }
}
