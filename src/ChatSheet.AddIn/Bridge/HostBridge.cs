using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Hosts;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Bridge
{
    /// <summary>
    /// 侧边栏 UI 与加载项之间的消息桥。
    /// UI 侧不持有任何密钥、也不直接发网络请求：密钥用 DPAPI 存在本地，
    /// 请求一律由加载项发起，所以全部能力都要经这里暴露。
    /// </summary>
    internal sealed class HostBridge : IDisposable
    {
        private readonly Func<object> _applicationAccessor;
        private readonly WorkbookContext _workbook;
        private AgentChannels _agentChannels;
        private CoreWebView2 _core;

        /// <summary>
        /// 创建本对象时所在的 UI 线程上下文。
        /// WebView2 只能从该线程访问，所有推送都要切回这里。
        /// </summary>
        private readonly SynchronizationContext _uiContext;

        private readonly Dictionary<string, Func<JObject, Task<object>>> _handlers =
            new Dictionary<string, Func<JObject, Task<object>>>(StringComparer.Ordinal);

        internal HostBridge(CoreWebView2 core, Func<object> applicationAccessor)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            // 构造发生在 WebView2 初始化完成的回调中，此时正处于 UI 线程。
            _uiContext = SynchronizationContext.Current;
            _applicationAccessor = applicationAccessor ?? throw new ArgumentNullException(nameof(applicationAccessor));
            _workbook = new WorkbookContext(_applicationAccessor);
            RegisterHandlers();

            // Agent 相关通道单独注册，把对话与设置逻辑与桥本身解耦。
            _agentChannels = new AgentChannels(
                _applicationAccessor,
                PushAgentUpdateAsync,
                PushRawAsync,
                InvokeOnUiAsync);
            _agentChannels.Register(_handlers);
        }

        /// <summary>
        /// 在 UI 线程上执行委托并返回结果。
        ///
        /// 宿主 COM 对象是 STA 绑定的，Agent 循环在 await 之后位于线程池线程，
        /// 一切触碰工作簿的操作都必须经此切回，否则跨单元调用会不稳定。
        /// </summary>
        private Task<object> InvokeOnUiAsync(Func<object> work)
        {
            if (_uiContext == null || SynchronizationContext.Current == _uiContext)
            {
                // 已在 UI 线程（或无上下文可用），直接执行。
                try
                {
                    return Task.FromResult(work());
                }
                catch (Exception ex)
                {
                    return FromException(ex);
                }
            }

            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _uiContext.Post(
                _ =>
                {
                    try
                    {
                        completion.TrySetResult(work());
                    }
                    catch (Exception ex)
                    {
                        // 异常要带回调用方，否则工具失败会表现为无响应。
                        completion.TrySetException(ex);
                    }
                },
                null);

            return completion.Task;
        }

        private static Task<object> FromException(Exception ex)
        {
            var completion = new TaskCompletionSource<object>();
            completion.SetException(ex);
            return completion.Task;
        }

        /// <summary>
        /// 宽度校准回调。由承载控件注入，因为只有它持有窗格对象。
        /// 入参为面板当前与目标 CSS 宽度、设备像素比，返回宿主实际采用的宽度值。
        /// </summary>
        internal Func<int, int, double, int> WidthAdjuster { get; set; }

        /// <summary>宽度存档回调，用户拖动结束后由面板触发。</summary>
        internal Func<int> WidthPersister { get; set; }

        /// <summary>
        /// 主题应用回调。面板定下主题后调用，用于给面板外那圈宿主控件上色并存档。
        /// 返回是否成功应用。
        /// </summary>
        internal Func<string, bool> ThemeApplier { get; set; }

        private Task PushAgentUpdateAsync(Agent.AgentUpdate update)
        {
            Post(new
            {
                kind = "agent",
                stage = update.Kind,
                text = update.Text,
                payload = update.Payload,
            });
            return Task.CompletedTask;
        }

        private Task PushRawAsync(object message)
        {
            Post(message);
            return Task.CompletedTask;
        }

        internal void Start()
        {
            _core.WebMessageReceived += OnWebMessageReceived;
        }

        private void RegisterHandlers()
        {
            _handlers["ping"] = _ => Task.FromResult<object>(new { pong = true, at = DateTime.Now.ToString("o") });
            _handlers["host.info"] = _ => Task.FromResult<object>(BuildHostInfo());
            // 面板侧无法访问文件系统，出问题时只能靠这个通道把状态写进加载项日志。
            _handlers["client.log"] = payload =>
            {
                var level = payload.Value<string>("level") ?? "info";
                var message = "[面板] " + (payload.Value<string>("message") ?? string.Empty);
                switch (level)
                {
                    case "error":
                        Log.Error(message, null);
                        break;
                    case "warn":
                        Log.Warn(message);
                        break;
                    default:
                        Log.Info(message);
                        break;
                }

                return Task.FromResult<object>(new { logged = true });
            };

            // 宽度自校准：面板报告自身 CSS 像素宽度、目标值与设备像素比，
            // 由加载项换算出宿主单位。这样无需在代码里假设 DPI 缩放。
            _handlers["pane.ensureWidth"] = payload =>
            {
                var currentCss = payload.Value<int?>("currentCss") ?? 0;
                var targetCss = payload.Value<int?>("targetCss") ?? 0;
                var dpr = payload.Value<double?>("devicePixelRatio") ?? 1.0;

                if (currentCss <= 0 || targetCss <= 0 || currentCss >= targetCss)
                {
                    return Task.FromResult<object>(new { adjusted = false, reason = "无需调整" });
                }

                var applied = WidthAdjuster?.Invoke(currentCss, targetCss, dpr) ?? -1;
                return Task.FromResult<object>(new { adjusted = applied > 0, hostWidth = applied });
            };

            // 记住用户拖动后的宽度，下次打开直接恢复，不再按视口反推。
            _handlers["pane.saveWidth"] = _ =>
            {
                var stored = WidthPersister?.Invoke() ?? -1;
                return Task.FromResult<object>(new { saved = stored > 0, hostWidth = stored });
            };

            // 面板报告当前主题。页面自己的配色由 CSS 处理，这里只管页面之外的部分：
            // 承载控件的底色、初始化时那块占位文字，以及 WebView2 的默认底色。
            // 存档是为了下次打开在页面加载之前就已经是对的颜色，否则深色下会先闪白。
            _handlers["pane.saveTheme"] = payload =>
            {
                var theme = payload.Value<string>("theme") ?? string.Empty;
                if (theme != "light" && theme != "dark")
                {
                    return Task.FromResult<object>(new { applied = false, reason = "未知主题：" + theme });
                }

                var applied = ThemeApplier?.Invoke(theme) ?? false;
                return Task.FromResult<object>(new { applied, theme });
            };

            _handlers["workbook.summary"] = _ =>
            {
                var summary = _workbook.GetSummary();
                return Task.FromResult<object>(new
                {
                    hasWorkbook = summary.HasWorkbook,
                    name = summary.Name,
                    saved = summary.Saved,
                    sheetCount = summary.SheetCount,
                    activeSheet = summary.ActiveSheet,
                    promptText = summary.ToPromptText(),
                });
            };

            _handlers["workbook.selection"] = _ =>
            {
                var selection = _workbook.GetSelection();
                return Task.FromResult<object>(new
                {
                    hasSelection = selection.HasSelection,
                    sheetName = selection.SheetName,
                    address = selection.Address,
                    rowCount = selection.RowCount,
                    columnCount = selection.ColumnCount,
                    promptText = selection.ToPromptText(),
                });
            };
        }

        private object BuildHostInfo()
        {
            var application = _applicationAccessor();
            var kind = HostProbe.Detect(application);
            return new
            {
                host = HostProbe.DisplayName(kind),
                hostKind = kind.ToString(),
                process = HostProbe.CurrentProcessName() + ".exe",
                bitness = Environment.Is64BitProcess ? "x64" : "x86",
                hostName = application == null ? string.Empty : Com.GetString(application, "Name"),
                hostVersion = application == null ? string.Empty : Com.GetString(application, "Version"),
                hostBuild = application == null ? string.Empty : Com.GetString(application, "Build"),
                webview2 = SafeBrowserVersion(),
                clr = Environment.Version.ToString(),
                logPath = Log.CurrentPath,
                addInVersion = typeof(HostBridge).Assembly.GetName().Version?.ToString() ?? string.Empty,
            };
        }

        private string SafeBrowserVersion()
        {
            try
            {
                return _core?.Environment?.BrowserVersionString ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string requestId = null;
            try
            {
                var raw = e.WebMessageAsJson;
                var envelope = JObject.Parse(raw);
                requestId = envelope.Value<string>("id");
                var channel = envelope.Value<string>("channel");
                var payload = envelope["payload"] as JObject ?? new JObject();

                if (string.IsNullOrEmpty(channel) || !_handlers.TryGetValue(channel, out var handler))
                {
                    Reply(requestId, false, null, $"未知通道：{channel}");
                    return;
                }

                var result = await handler(payload).ConfigureAwait(true);
                Reply(requestId, true, result, null);
            }
            catch (Exception ex)
            {
                Log.Error("处理面板消息失败", ex);
                Reply(requestId, false, null, ex.Message);
            }
        }

        private void Reply(string requestId, bool ok, object data, string error)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                return;
            }

            Post(new { kind = "response", id = requestId, ok, data, error });
        }

        /// <summary>主动通知面板切换路由，用于功能区的“设置”“诊断”按钮。</summary>
        internal void PostNavigate(string route)
        {
            Post(new { kind = "navigate", route });
        }

        /// <summary>
        /// 向面板发送消息。
        ///
        /// 必须回到 UI 线程：WebView2 的成员只能从创建它的 UI 线程访问。
        /// Agent 循环里的 await（HTTP 流式读取等）会把执行切到线程池线程，
        /// 此时直接调用会抛「CoreWebView2 members can only be accessed from
        /// the UI thread」——而且因为异常被这里吞掉，症状是界面完全没反应、
        /// 只在日志里留下一堆告警，极易误判为模型没响应。
        ///
        /// 在唯一出口统一切换线程，而不是去逐个修散落各处的 await，
        /// 这样新增推送路径不会重新引入同一问题。
        /// </summary>
        private void Post(object message)
        {
            try
            {
                var json = JsonConvert.SerializeObject(message);

                if (_uiContext != null && SynchronizationContext.Current != _uiContext)
                {
                    // Post 而非 Send：不阻塞 Agent 循环，且能保持消息顺序。
                    _uiContext.Post(_ => PostCore(json), null);
                    return;
                }

                PostCore(json);
            }
            catch (Exception ex)
            {
                Log.Warn("向面板发送消息失败：" + ex.Message);
            }
        }

        private void PostCore(string json)
        {
            try
            {
                _core?.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                Log.Warn("向面板发送消息失败（UI 线程）：" + ex.Message);
            }
        }

        public void Dispose()
        {
            try
            {
                if (_core != null)
                {
                    _core.WebMessageReceived -= OnWebMessageReceived;
                }

                // 必须先释放 Agent 通道：它会让挂起的审批以拒绝收束，
                // 否则正在等待用户决定的任务会永久卡住。
                _agentChannels?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _agentChannels = null;
                _core = null;
                _handlers.Clear();
            }
        }
    }
}
