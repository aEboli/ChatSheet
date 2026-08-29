using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Agent;
using ChatSheet.AddIn.Providers;
using ChatSheet.AddIn.Storage;
using ChatSheet.AddIn.Tools;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Bridge
{
    /// <summary>
    /// 面板与 Agent 之间的通道实现。
    ///
    /// 审批是这里最需要小心的部分：加载项发起审批请求后必须挂起等待
    /// 面板回传用户决定，用 TaskCompletionSource 按请求标识配对。
    /// 若面板在等待期间被关闭，必须让等待方以「拒绝」收束，
    /// 否则 Agent 会永久卡住。
    /// </summary>
    internal sealed class AgentChannels : IDisposable
    {
        private readonly AgentRunner _agent;
        private readonly Func<AgentUpdate, Task> _push;
        private readonly Func<object, Task> _pushRaw;

        /// <summary>
        /// 切到 UI 线程执行。撤销要访问宿主 COM 对象，
        /// 而通道回调运行在消息处理链上，未必是 UI 线程。
        /// </summary>
        private readonly Func<Func<object>, Task<object>> _uiInvoker;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _pendingApprovals =
            new ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>>(StringComparer.Ordinal);

        private CancellationTokenSource _currentRun;
        private int _approvalSequence;
        private Settings _settings;

        internal AgentChannels(
            Func<object> applicationAccessor,
            Func<AgentUpdate, Task> push,
            Func<object, Task> pushRaw,
            Func<Func<object>, Task<object>> uiInvoker)
        {
            _agent = new AgentRunner(applicationAccessor, uiInvoker);
            _push = push;
            _pushRaw = pushRaw;
            _uiInvoker = uiInvoker ?? (work => Task.FromResult(work()));
            _settings = Settings.Load();
        }

        internal void Register(IDictionary<string, Func<JObject, Task<object>>> handlers)
        {
            handlers["settings.get"] = _ => Task.FromResult(GetSettingsPayload());

            // 图片能力约束下发给面板，避免前端与后端各写一套上限。
            handlers["image.limits"] = _ => Task.FromResult<object>(new
            {
                maxCount = ImageSupport.MaxImagesPerTurn,
                maxBytes = ImageSupport.MaxBytesPerImage,
                mediaTypes = ImageSupport.SupportedMediaTypes,
            });

            // 文件附件的约束同理。面板据此在拖入时就给出拒绝原因，
            // 不必先发一轮再被这边退回。
            handlers["file.limits"] = _ => Task.FromResult<object>(new
            {
                maxCount = FileSupport.MaxFilesPerTurn,
                maxBytes = FileSupport.MaxBytesPerFile,
                maxTotalBytes = FileSupport.MaxTotalBytes,
                extensions = FileSupport.SupportedExtensions,
            });
            handlers["settings.save"] = SaveSettingsAsync;
            handlers["cli.probe"] = _ => Task.FromResult(ProbeCliPayload());
            handlers["models.list"] = ListModelsAsync;
            handlers["chat.send"] = SendAsync;
            handlers["chat.stop"] = _ => Task.FromResult(Stop());
            handlers["chat.reset"] = _ =>
            {
                _agent.Reset();
                return Task.FromResult<object>(new { reset = true });
            };
            handlers["approval.respond"] = RespondApprovalAsync;

            // 对话页的快捷切换：模型、思考档位、审批策略。
            // 这三项每次任务都可能调整，走完整的设置保存太重。
            handlers["session.update"] = payload =>
            {
                var settings = Settings.Load();
                var changed = new List<string>();

                var model = payload.Value<string>("model");
                if (model != null && !string.Equals(settings.Model, model.Trim(), StringComparison.Ordinal))
                {
                    settings.Model = model.Trim();
                    // 对话页的模型列表就是当前连接拉来的，选中即属于当前连接。
                    settings.StampModelConnection();
                    changed.Add("模型=" + settings.Model);
                }

                if (Thinking.TryParse(payload.Value<string>("thinking"), out var level) &&
                    settings.Thinking != level)
                {
                    settings.Thinking = level;
                    changed.Add("思考档位=" + level);
                }

                if (Enum.TryParse(payload.Value<string>("approval"), out ApprovalPolicy policy) &&
                    settings.Approval != policy)
                {
                    settings.Approval = policy;
                    changed.Add("审批策略=" + policy);
                }

                // 「只看名单」开关走这条通道而不是 settings.save：选择器在对话页，
                // 而 settings.save 发的是设置页那份 current 全量快照，且 initSettings
                // 每个面板生命周期只跑一次。让开关走那条路，用户在选择器里拨完再去
                // 设置页点保存，就会把面板启动时的旧值写回来。
                var onlyFavorites = payload.Value<bool?>("onlyFavoriteModels");
                if (onlyFavorites.HasValue && settings.OnlyFavoriteModels != onlyFavorites.Value)
                {
                    settings.OnlyFavoriteModels = onlyFavorites.Value;
                    changed.Add("只看名单=" + onlyFavorites.Value);
                }

                if (changed.Count > 0)
                {
                    settings.Save();
                    _settings = settings;
                    Log.Info("对话页快捷调整：" + string.Join("，", changed));
                }

                return Task.FromResult<object>(new
                {
                    model = settings.Model,
                    thinking = settings.Thinking.ToString(),
                    approval = settings.Approval.ToString(),
                    onlyFavoriteModels = settings.OnlyFavoriteModels,
                    thinkingSupported = Thinking.SupportedLevels(EffectiveProtocol()),
                });
            };

            // 常用名单的读写。名单是用户意图，落盘；判定是外部事实，只在内存里。
            handlers["models.favorites"] = payload =>
            {
                var settings = Settings.Load();
                var key = settings.FavoritesKey();
                var action = payload.Value<string>("action") ?? "get";
                var model = payload.Value<string>("model");

                switch (action)
                {
                    case "toggle":
                        FavoriteModels.Toggle(key, model);
                        break;
                    case "add":
                        // 手填的 ID 自动进名单：肯花力气打出来的 ID 就是要用的。
                        FavoriteModels.Add(key, model);
                        break;
                }

                return Task.FromResult<object>(new
                {
                    favorites = FavoriteModels.Load(key),
                    availability = AvailabilityPayload(settings),
                });
            };

            // 面板初次渲染时需要主动取一次上下文占用，
            // 否则进度圆环要等到第一轮对话产生推送后才有数值。
            handlers["context.state"] = _ =>
            {
                var used = _agent.Conversation.EstimateTotalTokens();
                var budget = Math.Max(1, _settings.ContextBudgetTokens);
                var ratio = Math.Min(1.0, (double)used / budget);

                return Task.FromResult<object>(new
                {
                    used,
                    budget,
                    ratio,
                    percent = (int)Math.Round(ratio * 100),
                    threshold = (int)(Conversation.CompressionThreshold * 100),
                    nearLimit = ratio >= Conversation.CompressionThreshold,
                });
            };

            // 「适配」按钮：把活动表的已用范围整片排好，不经过模型。
            //
            // 不走对话是刻意的：这是个确定性的排版动作，用户点按钮就是已经表达了
            // 意图，再让模型转述一遍只会增加延迟、token 开销和被误解的可能。
            // 但仍登记撤销记录并回传标识，面板据此给出撤销入口——
            // 加载项通过 COM 的写入会清空 Excel 自身的撤销栈，Ctrl+Z 救不回来。
            //
            // 不传 range：由 fit_range 自己取已用范围，省一次跨线程往返，
            // 也让「适配到哪」这个判断只存在一处。
            handlers["sheet.fit"] = async payload =>
            {
                var undoId = "fit-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var args = new JObject();

                // 水平对齐由面板给。缺省交给 fit_range 兜底成 center，
                // 这样「默认居中」只在一处定义。
                var alignment = payload.Value<string>("horizontalAlignment");
                if (!string.IsNullOrWhiteSpace(alignment))
                {
                    args["horizontal_alignment"] = alignment.Trim();
                }

                var result = (ToolResult)await _uiInvoker(
                    () => _agent.Tools.Execute("fit_range", args, undoId)).ConfigureAwait(false);

                if (!result.Ok)
                {
                    Log.Warn($"适配失败：{result.ErrorCode} {result.Error}");
                    return new { ok = false, message = result.Error };
                }

                var data = JObject.FromObject(result.Data);

                // 只有确实登记成了记录才回传标识。
                //
                // 这里曾经无条件回传：快照采集失败时没有记录，面板却照样显示
                // 撤销按钮，点下去只能得到「找不到该操作记录」。宁可不给按钮，
                // 也不能给一个注定失败的按钮——后者会让用户以为改动可以回退。
                var undoRecord = _agent.Tools.Undo.Find(undoId);
                var canUndo = undoRecord?.CanUndo == true;

                Log.Info($"适配 {data.Value<string>("address")}：{data.Value<int>("cells_affected")} 个单元格" +
                    (canUndo ? string.Empty : "（未登记撤销记录）"));

                return new
                {
                    ok = true,
                    undoId = canUndo ? undoId : null,
                    // 没有撤销入口时说明原因。缺按钮本身是可见的，缺原因则会
                    // 被当成故障——而它其实是「保不住足以完整还原的快照，
                    // 那就不承诺可以撤销」这一有意为之的取舍。
                    //
                    // 只说事实加最常见的成因，不逐一枚举：采集失败有几种情形
                    // （范围行列数过大、原排版逐格各异且单元格过多、宿主读取失败），
                    // 把三种都摆给用户并不能帮他做任何决定，具体原因记在日志里。
                    undoUnavailableReason = canUndo
                        ? null
                        : "这次适配不能撤销：范围太大，保不住足以完整还原的排版快照" +
                            "（原本的对齐逐格不同时尤其容易触发）。适配本身已经生效。",
                    address = data.Value<string>("address"),
                    sheet = data.Value<string>("sheet"),
                    rows = data.Value<int>("rows_adjusted"),
                    columns = data.Value<int>("columns_adjusted"),
                    horizontalAlignment = data.Value<string>("horizontal_alignment"),
                };
            };

            // 撤销与恢复。必须切到 UI 线程：还原要访问宿主 COM 对象。
            handlers["undo.apply"] = async payload =>
            {
                var id = payload.Value<string>("id");
                var redo = payload.Value<bool?>("redo") ?? false;

                if (string.IsNullOrEmpty(id))
                {
                    return new { ok = false, message = "缺少操作标识" };
                }

                var outcome = (UndoOutcome)await _uiInvoker(
                    () => redo ? _agent.Tools.Undo.Redo(id) : _agent.Tools.Undo.Undo(id)).ConfigureAwait(false);

                Log.Info($"{(redo ? "恢复" : "撤销")}操作 {id}：{(outcome.Ok ? "成功" : "失败 " + outcome.ErrorCode)} {outcome.Message}");

                return new
                {
                    ok = outcome.Ok,
                    message = outcome.Message,
                    errorCode = outcome.ErrorCode,
                    undone = outcome.Undone,
                };
            };

            // 手动压缩：圆环到达阈值后由用户决定是否立即压缩。
            handlers["context.compact"] = _ =>
            {
                var settings = Settings.Load();
                var trim = _agent.Conversation.TrimToBudget(settings.ContextBudgetTokens);
                Log.Info($"手动压缩上下文：{trim.TokensBefore} → {trim.TokensAfter} tokens");

                return Task.FromResult<object>(new
                {
                    trimmed = trim.Trimmed,
                    before = trim.TokensBefore,
                    after = trim.TokensAfter,
                    compressed = trim.CompressedToolResults,
                    dropped = trim.DroppedMessages,
                    budget = trim.BudgetTokens,
                });
            };
        }

        /// <summary>
        /// 当前连接下已有判定的模型，键是模型 ID，值是三态之一。
        ///
        /// 只下发已有判定的：没判定就是「未确认」，让面板自己把缺席渲染成那个状态，
        /// 省掉为整份目录逐个下发 Unknown。
        /// </summary>
        private static object AvailabilityPayload(Settings settings)
        {
            var payload = new JObject();
            foreach (var pair in ModelAvailability.SnapshotFor(settings.ConnectionKey()))
            {
                payload[pair.Key] = pair.Value.ToString();
            }

            return payload;
        }

        private object GetSettingsPayload()
        {
            var hasCustomToken = SecretStore.Exists(Settings.CustomApiSecretKey);
            var maskedToken = string.Empty;
            if (hasCustomToken)
            {
                // 只回传掩码，密钥本身绝不出加载项进程。
                maskedToken = SecretStore.Mask(SecretStore.Load(Settings.CustomApiSecretKey));
            }

            // 由后端给出权威的就绪判断：它才知道 CLI 配置里有没有模型、
            // 密钥是否真的能解开。前端自行推断会与实际不一致。
            var ready = false;
            var readyDetail = string.Empty;
            var effectiveModel = _settings.Model;

            try
            {
                var connection = _settings.ResolveConnection();
                effectiveModel = connection.Model;

                if (string.IsNullOrWhiteSpace(connection.Model))
                {
                    readyDetail = $"{connection.SourceLabel} 的配置未指定模型，请选择或填写模型名";
                }
                else
                {
                    ready = true;
                    readyDetail = $"{connection.SourceLabel} · {connection.BaseUrl} · {connection.Model}";
                }
            }
            catch (ProviderException ex)
            {
                readyDetail = ex.Message;
            }
            catch (Exception ex)
            {
                readyDetail = "配置解析失败：" + ex.Message;
            }

            return new
            {
                mode = _settings.Mode.ToString(),
                cliSource = _settings.CliSource.ToString(),
                customProtocol = Protocols.Get(_settings.CustomProtocol).Id,
                customBaseUrl = _settings.CustomBaseUrl,
                model = _settings.Model,
                // CLI 配置自带模型时，这里会是那个值，而 model 字段仍为空。
                effectiveModel,
                thinking = _settings.Thinking.ToString(),
                approval = _settings.Approval.ToString(),
                temperature = _settings.Temperature,
                maxOutputTokens = _settings.MaxOutputTokens,
                contextBudgetTokens = _settings.ContextBudgetTokens,
                maxSteps = _settings.MaxSteps,
                autoIncludeSelection = _settings.AutoIncludeSelection,
                toolProtocol = _settings.ToolProtocol.ToString(),
                visionRelayModel = _settings.VisionRelayModel,
                onlyFavoriteModels = _settings.OnlyFavoriteModels,
                favorites = FavoriteModels.Load(_settings.FavoritesKey()),
                // 三态由后端给权威判断，面板只做投影。
                availability = AvailabilityPayload(_settings),
                hasCustomToken,
                maskedToken,
                ready,
                readyDetail,
                protocols = ProtocolOptions(),
                thinkingOptions = ThinkingOptions(),
                // 当前协议实际支持的档位，界面据此标注哪些会被降级。
                thinkingSupported = Thinking.SupportedLevels(EffectiveProtocol()),
                approvalOptions = ApprovalOptions(),
                toolProtocolOptions = ToolProtocolOptions(),
            };
        }

        /// <summary>取当前生效的协议，用于判断思考档位支持范围。</summary>
        private ProtocolKind EffectiveProtocol()
        {
            try
            {
                return _settings.ResolveConnection().Protocol;
            }
            catch
            {
                return _settings.Mode == ConnectionMode.CustomApi
                    ? _settings.CustomProtocol
                    : Protocols.Default;
            }
        }

        private static object ThinkingOptions()
        {
            var list = new List<object>();
            foreach (var option in Thinking.Options)
            {
                list.Add(new { id = option.Id, label = option.Label, hint = option.Hint });
            }

            return list;
        }

        private static object ApprovalOptions()
        {
            return new[]
            {
                new { id = "PerWrite", label = "逐项审批", hint = "写操作逐项确认，读操作自动执行" },
                new { id = "PerTurn", label = "每轮确认", hint = "每轮开始前统一确认一次" },
                new { id = "Automatic", label = "全自动", hint = "不询问，依赖 Excel 撤销兜底" },
            };
        }

        private static object ToolProtocolOptions()
        {
            return new[]
            {
                new { id = "Auto", label = "自动探测", hint = "先按原生方式发，被拒或模型推辞后自动改用文本指令" },
                new { id = "Native", label = "原生函数调用", hint = "多数模型支持，效果最好" },
                new { id = "Text", label = "文本指令", hint = "把工具清单写进提示词，适合不支持函数调用的模型" },
                new { id = "None", label = "不用工具", hint = "只给方案与公式，不读写表格" },
            };
        }

        private static object ProtocolOptions()
        {
            var list = new List<object>();
            foreach (var protocol in Protocols.All)
            {
                list.Add(new { id = protocol.Id, label = protocol.Label });
            }

            return list;
        }

        private Task<object> SaveSettingsAsync(JObject payload)
        {
            var settings = Settings.Load();
            var previousConnectionKey = settings.ConnectionKey();
            // 面板确认「这个模型是在当前这套接入配置下选的」。缺少确认时，
            // 一旦连接发生变化就只能当作上一套配置的残留处理。
            var modelChosenForConnection = payload.Value<bool?>("modelChosenForConnection") ?? false;

            if (Enum.TryParse(payload.Value<string>("mode"), out ConnectionMode mode)) { settings.Mode = mode; }
            if (Enum.TryParse(payload.Value<string>("cliSource"), out CliKind cli)) { settings.CliSource = cli; }
            if (Protocols.TryParse(payload.Value<string>("customProtocol"), out var protocol)) { settings.CustomProtocol = protocol; }
            if (Thinking.TryParse(payload.Value<string>("thinking"), out var thinking)) { settings.Thinking = thinking; }
            if (Enum.TryParse(payload.Value<string>("approval"), out ApprovalPolicy approval)) { settings.Approval = approval; }

            if (payload["customBaseUrl"] != null) { settings.CustomBaseUrl = payload.Value<string>("customBaseUrl") ?? string.Empty; }
            if (payload["model"] != null) { settings.Model = payload.Value<string>("model") ?? string.Empty; }
            // 模型归属必须在协议、地址、CLI 来源都写完后再判定，
            // 否则算出的连接键还是旧的。
            settings.KeepModelOnlyIfChosenForConnection(previousConnectionKey, modelChosenForConnection);
            if (payload["temperature"] != null)
            {
                settings.Temperature = payload["temperature"].Type == JTokenType.Null
                    ? (double?)null
                    : payload.Value<double>("temperature");
            }

            if (payload["maxOutputTokens"] != null) { settings.MaxOutputTokens = payload.Value<int>("maxOutputTokens"); }
            if (payload["contextBudgetTokens"] != null) { settings.ContextBudgetTokens = payload.Value<int>("contextBudgetTokens"); }
            if (payload["maxSteps"] != null) { settings.MaxSteps = payload.Value<int>("maxSteps"); }
            if (payload["autoIncludeSelection"] != null) { settings.AutoIncludeSelection = payload.Value<bool>("autoIncludeSelection"); }
            if (Enum.TryParse(payload.Value<string>("toolProtocol"), out ToolProtocolPreference toolProtocol))
            {
                // 用户改了工具形态就把探测结果作废：从「文本指令」改回「自动探测」时，
                // 留着上次探出的降级档等于这个选项没生效。
                if (settings.ToolProtocol != toolProtocol)
                {
                    ModelCapabilities.Reset();
                }

                settings.ToolProtocol = toolProtocol;
            }

            if (payload["visionRelayModel"] != null)
            {
                settings.VisionRelayModel = payload.Value<string>("visionRelayModel") ?? string.Empty;
            }

            // 密钥单独走加密存储；面板传空字符串表示清除。
            var token = payload.Value<string>("customToken");
            if (token != null)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    SecretStore.Delete(Settings.CustomApiSecretKey);
                }
                else
                {
                    SecretStore.Save(Settings.CustomApiSecretKey, token.Trim());
                }

                // 写了密钥就作废该连接的可用性判定：一个账号能碰到哪些模型跟着密钥走。
                // 按「写了密钥」触发而不去比对新旧——比对要把已存的密钥读回来，
                // 无谓地多碰一次密钥，而多作废一次只是让下一轮重新记一遍。
                //
                // 只有自定义接口这条路会经过这里。本机 CLI 的密钥在 CLI 自己的配置里、
                // 不经 SecretStore，那条路上没有这个触发点——不是漏了。
                ModelAvailability.ResetConnection(settings.ConnectionKey());
            }

            // 换了连接同样作废：判定的键含连接，旧连接那份留着也不会被查到，
            // 但新连接可能与某个旧连接同键（改回来），那时留着的就是过期结论。
            if (previousConnectionKey != settings.ConnectionKey())
            {
                ModelAvailability.ResetConnection(settings.ConnectionKey());
            }

            settings.Save();
            _settings = settings;

            return Task.FromResult(GetSettingsPayload());
        }

        private object ProbeCliPayload()
        {
            var list = new List<object>();
            foreach (var probe in LocalCliConfig.Probe())
            {
                list.Add(new
                {
                    kind = probe.Kind.ToString(),
                    displayName = probe.DisplayName,
                    configPath = probe.ConfigPath,
                    exists = probe.Exists,
                    usable = probe.Usable,
                    protocol = Protocols.Get(probe.Protocol).Id,
                    baseUrl = probe.BaseUrl,
                    model = probe.Model,
                    detail = probe.Detail,
                });
            }

            return new { candidates = list };
        }

        private async Task<object> ListModelsAsync(JObject payload)
        {
            var settings = Settings.Load();

            // 面板可能已切换模式但尚未保存，必须按它当前选择的模式解析，
            // 否则会拿旧设置去连（例如界面已切到本机 CLI，却仍按空的自定义地址解析而报错）。
            if (Enum.TryParse(payload.Value<string>("mode"), out ConnectionMode pendingMode))
            {
                settings.Mode = pendingMode;
            }

            if (Enum.TryParse(payload.Value<string>("cliSource"), out CliKind pendingCli))
            {
                settings.CliSource = pendingCli;
            }

            // 允许面板传入尚未保存的地址与密钥，以便保存前先试连。
            var protocolId = payload.Value<string>("protocol");
            var baseUrlInput = payload.Value<string>("baseUrl");
            var tokenInput = payload.Value<string>("token");

            ProtocolKind protocol;
            string baseUrl;
            string token;

            if (settings.Mode == ConnectionMode.CustomApi && !string.IsNullOrWhiteSpace(baseUrlInput))
            {
                protocol = Protocols.TryParse(protocolId, out var parsed) ? parsed : settings.CustomProtocol;
                baseUrl = Protocols.NormalizeBaseUrl(baseUrlInput, protocol);
                token = string.IsNullOrWhiteSpace(tokenInput)
                    ? SecretStore.Load(Settings.CustomApiSecretKey)
                    : tokenInput.Trim();
            }
            else
            {
                var connection = settings.ResolveConnection();
                protocol = connection.Protocol;
                baseUrl = connection.BaseUrl;
                token = connection.Token;
            }

            // 预算 = 单次请求的 30 秒 + 全部重试的退避时长。
            // 只给 30 秒会让重试还没走完就被超时掐断。
            var budget = TimeSpan.FromSeconds(30) + RetryPolicy.TotalBackoff;

            using (var client = new ChatClient())
            using (var cts = new CancellationTokenSource(budget))
            {
                var models = await client.ListModelsAsync(
                    protocol,
                    baseUrl,
                    token,
                    cts.Token,
                    // 重试期间界面仍显示「获取中…」，不说明就像卡住了。
                    (attempt, delay, reason) => _pushRaw(new
                    {
                        kind = "models-retry",
                        text = RetryPolicy.Describe(attempt, delay, reason),
                        attempt,
                        maxRetries = RetryPolicy.MaxRetries,
                    })).ConfigureAwait(false);

                return new
                {
                    protocol = Protocols.Get(protocol).Id,
                    baseUrl,
                    models,
                    // 空列表不是错误：部分网关不提供模型列表，需允许手填。
                    manualEntryRequired = models.Count == 0,
                };
            }
        }

        private object Stop()
        {
            var cts = _currentRun;
            if (cts == null)
            {
                return new { stopped = false, reason = "当前没有进行中的任务" };
            }

            try
            {
                cts.Cancel();
                return new { stopped = true };
            }
            catch (Exception ex)
            {
                return new { stopped = false, reason = ex.Message };
            }
        }

        private async Task<object> SendAsync(JObject payload)
        {
            var input = payload.Value<string>("text");
            var images = ParseImages(payload);
            var files = ParseFiles(payload);

            // 文件内容拼进用户输入。图片走协议的多模态字段，文件走文本——
            // 四种协议都没有「文本文件」这类内容块，带围栏的代码块才是通用形式。
            var composed = FileSupport.Compose(input, files);

            // 同一时刻只跑一轮。面板侧会把处理中的新输入排进队列并在上一轮
            // 结束后自动接着发，因此正常使用不会撞上这里；真撞上说明有第二个
            // 入口绕过了队列（例如面板刷新后旧的请求仍在途），此时如实回报，
            // 而不是让两轮交替写同一个工作簿。
            if (_currentRun != null)
            {
                throw new ProviderException("BUSY", "上一轮任务尚未结束，请稍后重发这条内容。");
            }

            _settings = Settings.Load();

            // 记录本轮的接入配置（不含密钥），这是排查「发了没反应」的第一现场。
            try
            {
                var connection = _settings.ResolveConnection();
                Log.Info($"开始对话：模式={_settings.Mode} 来源={connection.SourceLabel} " +
                    $"协议={Protocols.Get(connection.Protocol).Id} 地址={connection.BaseUrl} " +
                    $"模型={connection.Model} 思考={_settings.Thinking} 审批={_settings.Approval} " +
                    $"输入长度={input?.Length ?? 0}" +
                    // 拼接后的长度单独记：只看输入长度会以为用户只发了一句话，
                    // 而实际进上下文的可能是几万字符的附件。
                    (files.Count > 0 ? $" 拼接后长度={composed.Length}" : string.Empty));

                if (files.Count > 0)
                {
                    Log.Info($"本轮附带 {FileSupport.Describe(files)}");
                }
            }
            catch (ProviderException ex)
            {
                // 配置不完整时直接回报，避免用户面对无反馈的界面。
                Log.Warn($"接入配置不可用：{ex.Code} {ex.Message}");
                await _push(new AgentUpdate { Kind = "error", Text = ex.Message, Payload = new { code = ex.Code } })
                    .ConfigureAwait(false);
                return new { completed = false, error = ex.Message, code = ex.Code };
            }

            var cts = new CancellationTokenSource();
            _currentRun = cts;

            try
            {
                await _agent.RunAsync(
                    composed,
                    _settings,
                    _push,
                    RequestApprovalAsync,
                    cts.Token,
                    images).ConfigureAwait(false);

                return new { completed = true };
            }
            catch (OperationCanceledException)
            {
                await _push(new AgentUpdate { Kind = "stopped", Text = "已停止生成。" }).ConfigureAwait(false);
                return new { completed = false, stopped = true };
            }
            catch (ProviderException ex)
            {
                // 必须记日志：失败的一轮此前只把消息推给面板，加载项日志里
                // 「开始对话」之后再无下文，事后无从判断是没发出去还是被拒绝。
                Log.Warn($"对话失败：{ex.Code} {ex.Message}");
                await _push(new AgentUpdate { Kind = "error", Text = ex.Message, Payload = new { code = ex.Code } })
                    .ConfigureAwait(false);
                return new { completed = false, error = ex.Message, code = ex.Code };
            }
            catch (Exception ex)
            {
                Log.Error("Agent 运行失败", ex);
                await _push(new AgentUpdate { Kind = "error", Text = "运行失败：" + ex.Message }).ConfigureAwait(false);
                return new { completed = false, error = ex.Message };
            }
            finally
            {
                _currentRun = null;
                cts.Dispose();
                FailPendingApprovals("任务已结束");
            }
        }

        /// <summary>
        /// 解析面板传来的图片。
        ///
        /// 单张不合规就整轮拒绝，而不是静默丢弃：用户以为图片发出去了、
        /// 模型却看不到，那比明确报错更难排查。
        /// </summary>
        private static List<ImageAttachment> ParseImages(JObject payload)
        {
            var result = new List<ImageAttachment>();
            if (!(payload["images"] is JArray array) || array.Count == 0)
            {
                return result;
            }

            if (array.Count > ImageSupport.MaxImagesPerTurn)
            {
                throw new ProviderException(
                    "TOO_MANY_IMAGES",
                    $"一次最多附带 {ImageSupport.MaxImagesPerTurn} 张图片，当前有 {array.Count} 张。");
            }

            foreach (var item in array)
            {
                if (!(item is JObject image))
                {
                    continue;
                }

                var name = image.Value<string>("name") ?? "图片";
                var dataUrl = image.Value<string>("dataUrl");
                result.Add(ImageSupport.ParseDataUrl(dataUrl, name));
            }

            return result;
        }

        /// <summary>
        /// 解析面板传来的文本文件。
        ///
        /// 与图片同样的取舍：一个不合规就整轮拒绝。静默丢弃会让用户以为
        /// 文件发出去了，而模型的回答其实完全没看过它。
        /// </summary>
        private static List<TextAttachment> ParseFiles(JObject payload)
        {
            var result = new List<TextAttachment>();
            if (!(payload["files"] is JArray array) || array.Count == 0)
            {
                return result;
            }

            if (array.Count > FileSupport.MaxFilesPerTurn)
            {
                throw new ProviderException(
                    "TOO_MANY_FILES",
                    $"一次最多附带 {FileSupport.MaxFilesPerTurn} 个文件，当前有 {array.Count} 个。");
            }

            var total = 0;
            foreach (var item in array)
            {
                if (!(item is JObject file))
                {
                    continue;
                }

                var attachment = FileSupport.Create(
                    file.Value<string>("name"),
                    file.Value<string>("text"));

                total += attachment.ByteLength;
                if (total > FileSupport.MaxTotalBytes)
                {
                    throw new ProviderException(
                        "FILES_TOO_LARGE",
                        $"文件合计 {total / 1024.0:F0} KB，超过 {FileSupport.MaxTotalBytes / 1024} KB 上限。" +
                            "文件内容会整段进入上下文，因此总量也有限制。");
                }

                result.Add(attachment);
            }

            return result;
        }

        private async Task<ApprovalDecision> RequestApprovalAsync(
            ToolDefinition definition,
            JObject args,
            ImpactEstimate impact)
        {
            var id = "ap" + Interlocked.Increment(ref _approvalSequence);
            var completion = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingApprovals[id] = completion;

            await _pushRaw(new
            {
                kind = "approval-request",
                id,
                tool = definition.Name,
                description = definition.Description,
                risk = definition.Risk.ToString(),
                impact = impact?.Text ?? string.Empty,
                // 探到范围时另给结构化字段，面板据此把地址译成行列说明。
                impactRange = string.IsNullOrEmpty(impact?.Address)
                    ? null
                    : new
                    {
                        sheet = impact.SheetName,
                        address = impact.Address,
                        cells = impact.CellCount,
                    },
                args,
            }).ConfigureAwait(false);

            return await completion.Task.ConfigureAwait(false);
        }

        private Task<object> RespondApprovalAsync(JObject payload)
        {
            var id = payload.Value<string>("id");
            if (string.IsNullOrEmpty(id) || !_pendingApprovals.TryRemove(id, out var completion))
            {
                return Task.FromResult<object>(new { accepted = false, reason = "该审批请求已失效" });
            }

            completion.TrySetResult(new ApprovalDecision
            {
                Approved = payload.Value<bool?>("approved") ?? false,
                Reason = payload.Value<string>("reason"),
                ApproveRest = payload.Value<bool?>("approveRest") ?? false,
            });

            return Task.FromResult<object>(new { accepted = true });
        }

        /// <summary>
        /// 让所有挂起的审批以拒绝收束。
        /// 面板关闭或任务结束时必须调用，否则 Agent 侧会永久等待。
        /// </summary>
        private void FailPendingApprovals(string reason)
        {
            foreach (var key in new List<string>(_pendingApprovals.Keys))
            {
                if (_pendingApprovals.TryRemove(key, out var completion))
                {
                    completion.TrySetResult(new ApprovalDecision { Approved = false, Reason = reason });
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _currentRun?.Cancel();
            }
            catch
            {
            }

            FailPendingApprovals("面板已关闭");
        }
    }
}
