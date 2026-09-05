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

        /// <summary>
        /// 批量确认的取消源。
        ///
        /// 与 _currentRun 分开是硬要求：Stop() Cancel 的是 _currentRun 这一个对话槽位，
        /// 复用它会让「停止」在批量与对话之间产生歧义——那正是「发消息不再误停当前
        /// 任务」已经付过代价的故障。停批量不许碰对话，反之同理。
        /// </summary>
        private CancellationTokenSource _currentBulkProbe;

        private int _approvalSequence;
        private Settings _settings;

        internal AgentChannels(
            Func<object> applicationAccessor,
            Func<AgentUpdate, Task> push,
            Func<object, Task> pushRaw,
            Func<Func<object>, Task<object>> uiInvoker)
        {
            if (applicationAccessor == null)
            {
                throw new ArgumentNullException(nameof(applicationAccessor));
            }

            _agent = new AgentRunner(applicationAccessor, uiInvoker);
            _push = push;
            _pushRaw = pushRaw;
            _uiInvoker = uiInvoker ?? (work => Task.FromResult(work()));
            _settings = Settings.Load();
        }

        internal void Register(IDictionary<string, Func<JObject, Task<object>>> handlers)
        {
            handlers["settings.get"] = _ => Task.FromResult(GetSettingsPayload());

            // 卡片上的范围不是死文字：用户要在允许之前亲眼看看那几格。
            // 必须经 UI 线程访问宿主 COM；解析失败返回现有 RangeResolver 错误，
            // 不静默跳到当前表的什么位置。
            handlers["sheet.goto"] = async payload =>
            {
                var address = payload.Value<string>("address");
                var sheet = payload.Value<string>("sheet");
                if (string.IsNullOrWhiteSpace(address))
                {
                    return new { ok = false, message = "缺少范围地址" };
                }

                try
                {
                    var result = (ToolResult)await _uiInvoker(
                        () => _agent.Tools.GotoRange(address, sheet)).ConfigureAwait(false);

                    return result.Ok
                        ? new { ok = true, data = result.Data }
                        : new { ok = false, message = result.Error, errorCode = result.ErrorCode };
                }
                catch (ToolException ex)
                {
                    return new { ok = false, message = ex.Message, errorCode = ex.Code };
                }
                catch (Exception ex)
                {
                    Log.Error("跳转范围失败", ex);
                    return new { ok = false, message = "无法跳转到该范围：" + ex.Message };
                }
            };

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

            // 收回本轮授权。不停止本轮——用户想停的是自动放行，不是整个任务。
            handlers["approval.revoke"] = _ =>
            {
                var count = _agent.RevokeApprovalGrants();
                Log.Info($"用户收回本轮授权，清掉 {count} 条");
                return Task.FromResult<object>(new { ok = true, revoked = count });
            };

            // 对话页的快捷切换：模型、思考档位、审批策略。
            // 这三项每次任务都可能调整，走完整的设置保存太重。
            handlers["session.update"] = payload =>
            {
                // 改正在跑的那一份设置对象，不要 Load 一份新的再替换引用。
                // AgentRunner 开轮时拿到的就是 _settings，换引用的话轮内切策略
                // 永远进不了正在跑的那一轮——规范要求下一刀写操作按新策略走。
                var settings = _settings ?? Settings.Load();
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

            handlers["models.probe"] = ProbeModelAsync;
            handlers["models.probe.bulk"] = ProbeFavoritesAsync;
            // 批量测试整份目录。与 models.probe.bulk 分开注册而不是加个参数：
            // 二者的作用范围与代价差一个数量级（名单几个 vs 目录几十个），
            // 而「几十次计费请求」这件事必须在调用点就看得见，不该藏在一个布尔里。
            handlers["models.test.all"] = TestAllModelsAsync;
            handlers["models.probe.stop"] = _ => Task.FromResult(StopBulkProbe());

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

                // 用户已经看过重叠警告并选择继续。只对撤销有意义：
                // 恢复走的是「后快照」，不存在盖掉更晚改动的问题。
                var force = payload.Value<bool?>("force") ?? false;

                if (string.IsNullOrEmpty(id))
                {
                    return new { ok = false, message = "缺少操作标识" };
                }

                var outcome = (UndoOutcome)await _uiInvoker(
                    () => redo ? _agent.Tools.Undo.Redo(id) : _agent.Tools.Undo.Undo(id, force)).ConfigureAwait(false);

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

        /// <summary>
        /// 确认一个模型。
        ///
        /// 只走已保存设置，不接受面板传来的候选配置。ListModelsAsync 刻意接受未保存值
        /// 让设置页能试连，但那条路对探测不成立：判定的键取自已保存的连接，
        /// 拿候选网关探出来的结论会盖到用户当前正在用的连接上。
        /// </summary>
        private async Task<object> ProbeModelAsync(JObject payload)
        {
            var model = (payload.Value<string>("model") ?? string.Empty).Trim();
            if (model.Length == 0)
            {
                throw new ProviderException("MODEL_REQUIRED", "没有指定要确认的模型。");
            }

            // 对话在飞时不许探测。SendAsync 的 BUSY 守卫只在它自己内部，
            // 这条通道继承不到——而几条请求压在同一个账号上会招限流，
            // 限流判未知等于花了钱没答案，还可能把用户那一轮带着上下文的请求限掉。
            if (_currentRun != null)
            {
                throw new ProviderException(
                    "BUSY", "正在对话中，等这一轮结束再确认模型。");
            }

            var settings = Settings.Load();
            var connection = settings.ResolveConnection();
            var connectionKey = settings.ConnectionKey();
            var capability = ModelCapabilities.For(connectionKey, model);

            var verdict = await ModelProbe.ProbeAsync(
                connection, model, capability.OutputLimit, CancellationToken.None)
                .ConfigureAwait(false);

            ModelAvailability.Record(connectionKey, model, verdict);
            Log.Info($"确认模型 {model}：{verdict}");

            return new
            {
                model,
                verdict = verdict.ToString(),
                availability = AvailabilityPayload(settings),
            };
        }

        /// <summary>
        /// 把名单里的模型逐个确认完。串行、带进度、可中断，已得结果保留。
        ///
        /// 只对名单提供批量，不对完整目录：几十个 ID 就是几十次付费请求，
        /// 而那正是用户想避开的开销。
        /// </summary>
        private async Task<object> ProbeFavoritesAsync(JObject payload)
        {
            if (_currentRun != null)
            {
                throw new ProviderException(
                    "BUSY", "正在对话中，等这一轮结束再确认模型。");
            }

            if (_currentBulkProbe != null)
            {
                throw new ProviderException("BUSY", "已经在确认名单了。");
            }

            var settings = Settings.Load();
            var connection = settings.ResolveConnection();
            var connectionKey = settings.ConnectionKey();
            var models = FavoriteModels.Load(settings.FavoritesKey());

            if (models.Count == 0)
            {
                return new { confirmed = 0, total = 0, stopped = false, availability = AvailabilityPayload(settings) };
            }

            var cts = new CancellationTokenSource();
            _currentBulkProbe = cts;

            var confirmed = 0;
            var stopped = false;

            try
            {
                for (var i = 0; i < models.Count; i++)
                {
                    if (cts.IsCancellationRequested)
                    {
                        stopped = true;
                        break;
                    }

                    var model = models[i];

                    await _pushRaw(new
                    {
                        kind = "probe-progress",
                        model,
                        index = i + 1,
                        total = models.Count,
                        starting = true,
                    }).ConfigureAwait(false);

                    AvailabilityVerdict? settled = null;
                    try
                    {
                        var verdict = await ModelProbe.ProbeAsync(
                            connection, model, ModelCapabilities.For(connectionKey, model).OutputLimit, cts.Token)
                            .ConfigureAwait(false);

                        ModelAvailability.Record(connectionKey, model, verdict);
                        settled = verdict;
                        confirmed++;
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户停了。已得结果保留，剩下的不发。
                        stopped = true;
                        break;
                    }
                    catch (ProviderException ex)
                    {
                        // 单个失败不该中断整批：它本身就是一条判定。
                        var verdict = ModelAvailability.Classify(ex, model);
                        ModelAvailability.Record(connectionKey, model, verdict);
                        settled = verdict;
                        confirmed++;
                    }

                    // 探完这一个就把判定推出去，让这一行当场变绿或变红。
                    //
                    // 不推的话，颜色只能等整批结束时由回复里的 availability 一次性补上，
                    // 而名单有十几个模型就要跑十几次往返——中途一列全是「未确认」，
                    // 用户看到的就是「批量探测点了没反应，失败的没变红、成功的没标绿」。
                    // 批量测试那条路（TestAllModelsAsync）一直是这么推的，这里是漏的。
                    //
                    // settled 标住「这一个已经有结论」：面板据此把扫光从这一行摘掉，
                    // 否则已经上色的行会继续挂着「正在测」的高光。
                    if (settled.HasValue)
                    {
                        await _pushRaw(new
                        {
                            kind = "probe-progress",
                            model,
                            index = i + 1,
                            total = models.Count,
                            verdict = settled.Value.ToString(),
                            settled = true,
                        }).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _currentBulkProbe = null;
                cts.Dispose();
            }

            Log.Info($"批量确认结束：已确认 {confirmed}/{models.Count}" + (stopped ? "（用户中止）" : string.Empty));

            await _pushRaw(new
            {
                kind = "probe-progress",
                model = string.Empty,
                index = confirmed,
                total = models.Count,
                done = true,
            }).ConfigureAwait(false);

            return new
            {
                confirmed,
                total = models.Count,
                stopped,
                availability = AvailabilityPayload(settings),
            };
        }

        /// <summary>
        /// 测试整份目录：每个模型一条最小请求，按给定并发跑。
        ///
        /// 与 ProbeFavoritesAsync 的区别不只是范围：
        ///   · 范围是面板传来的整份目录，不是落盘的名单。目录来自 GET /models，
        ///     后端并不留存，所以由面板把它带过来——它决定探哪些模型，
        ///     而这是用户点按钮时就已经表达的意图。
        ///   · 并发跑（默认 5），不是串行。并发确实会招限流，而限流判「未确认」；
        ///     这个代价由面板写在按钮上，让用户点之前就知道会发多少条。
        ///
        /// 停止走同一个 models.probe.stop：对用户而言「停下正在跑的那一批」只有一个意思，
        /// 两个停止通道反而要先想清楚停的是哪一批。
        /// </summary>
        private async Task<object> TestAllModelsAsync(JObject payload)
        {
            if (_currentRun != null)
            {
                throw new ProviderException(
                    "BUSY", "正在对话中，等这一轮结束再测试模型。");
            }

            if (_currentBulkProbe != null)
            {
                throw new ProviderException("BUSY", "已经在测试了。");
            }

            var settings = Settings.Load();
            var connection = settings.ResolveConnection();
            var connectionKey = settings.ConnectionKey();

            var models = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in payload?["models"] as JArray ?? new JArray())
            {
                var id = (token?.ToString() ?? string.Empty).Trim();
                if (id.Length > 0 && seen.Add(id))
                {
                    models.Add(id);
                }
            }

            if (models.Count == 0)
            {
                return new
                {
                    confirmed = 0,
                    total = 0,
                    stopped = false,
                    availability = AvailabilityPayload(settings),
                };
            }

            var concurrency = (int?)payload?["concurrency"] ?? 5;

            var cts = new CancellationTokenSource();
            _currentBulkProbe = cts;

            var stopped = false;
            var confirmed = 0;

            try
            {
                await _pushRaw(new
                {
                    kind = "probe-progress",
                    model = string.Empty,
                    index = 0,
                    total = models.Count,
                }).ConfigureAwait(false);

                await ModelProbe.ProbeManyAsync(
                    connection,
                    models,
                    concurrency,
                    model => ModelCapabilities.For(connectionKey, model).OutputLimit,
                    async (model, verdict, completed) =>
                    {
                        ModelAvailability.Record(connectionKey, model, verdict);
                        Interlocked.Increment(ref confirmed);

                        // 每探完一个就推一次：几十个模型跑下来要一阵，
                        // 只在结束时推一次的话中途看起来像卡住。
                        //
                        // settled 标住「这一个有结论了」，面板据此把它从「正在测」里摘掉。
                        // 少了这个字段，扫光会留在一行刚刚变绿或变红的行上——已经有结论
                        // 却还挂着「正在测」的高光，而真正在飞的那几个一个都没标。
                        await _pushRaw(new
                        {
                            kind = "probe-progress",
                            model,
                            index = completed,
                            total = models.Count,
                            verdict = verdict.ToString(),
                            settled = true,
                        }).ConfigureAwait(false);
                    },
                    cts.Token,
                    // 某个模型真的开始探时推一条不带判定的：并发 5 就是同时五行在测，
                    // 而「在飞的是哪几个」只有这里说得出来。不带 index——已完成数由
                    // onResult 那条推进，这条只负责把这一行标成正在测。
                    async model =>
                    {
                        await _pushRaw(new
                        {
                            kind = "probe-progress",
                            model,
                            total = models.Count,
                            starting = true,
                        }).ConfigureAwait(false);
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户停了。已得判定保留——那些请求已经付过钱了。
                stopped = true;
            }
            finally
            {
                _currentBulkProbe = null;
                cts.Dispose();
            }

            Log.Info(
                $"批量测试结束：已测 {confirmed}/{models.Count}，并发 {concurrency}" +
                (stopped ? "（用户中止）" : string.Empty));

            await _pushRaw(new
            {
                kind = "probe-progress",
                model = string.Empty,
                index = confirmed,
                total = models.Count,
                done = true,
            }).ConfigureAwait(false);

            return new
            {
                confirmed,
                total = models.Count,
                stopped,
                availability = AvailabilityPayload(settings),
            };
        }

        /// <summary>
        /// 停批量。刻意不碰 _currentRun——停批量与停对话是两件事。
        /// </summary>
        private object StopBulkProbe()
        {
            var running = _currentBulkProbe;
            if (running == null)
            {
                return new { stopped = false, reason = "没有正在进行的批量确认" };
            }

            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 刚好自己结束了，等价于已停。
            }

            Log.Info("用户中止批量确认");
            return new { stopped = true };
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
                new { id = "PerTurn", label = "每轮确认", hint = "本轮第一次写操作问一次，之后同一工作表同一类不再问；结构单独问" },
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
                impactNote = impact?.Note,
                // 探到范围时另给结构化字段，面板据此把地址译成行列说明。
                impactRange = string.IsNullOrEmpty(impact?.Address)
                    ? null
                    : new
                    {
                        sheet = impact.SheetName,
                        address = impact.Address,
                        cells = impact.CellCount,
                    },

                // 「将改成什么」的截断对照。只在这条推送里出现：
                // 写进工具结果或对话历史，等于每步批准都再付一次读取的税，
                // 而最近几条消息本来就不参与上下文压缩。
                preview = impact?.Preview == null
                    ? null
                    : new
                    {
                        currentUnreadable = impact.Preview.CurrentUnreadable,
                        formattingMixed = impact.Preview.FormattingMixed,
                        omittedCells = impact.Preview.OmittedCells,
                        discardedValues = impact.Preview.DiscardedValues,
                        kind = impact.Preview.Kind,
                        cells = impact.Preview.Cells.ConvertAll(cell => new
                        {
                            row = cell.Row,
                            column = cell.Column,
                            before = cell.Before,
                            after = cell.After,
                            beforeEmpty = cell.BeforeEmpty,
                            afterEmpty = cell.AfterEmpty,
                        }),
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
                ApproveStructureRest = payload.Value<bool?>("approveStructureRest") ?? false,
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
