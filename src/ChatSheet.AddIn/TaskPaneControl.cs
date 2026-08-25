using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ChatSheet.AddIn.Bridge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChatSheet.AddIn
{
    /// <summary>
    /// 侧边栏宿主控件。由 ICTPFactory.CreateCTP 按 ProgID 经 COM 实例化，
    /// 因此必须注册为 ActiveX 控件（CLSID 下带 Control 子键）。
    /// </summary>
    [ComVisible(true)]
    [Guid(ComIds.TaskPaneClsid)]
    [ProgId(ComIds.TaskPaneProgId)]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(ITaskPaneControl))]
    public sealed class TaskPaneControl : UserControl, ITaskPaneControl
    {
        /// <summary>
        /// 兜底用：部分宿主的 ContentControl 包装无法转回托管类型，
        /// 此时用最近创建的实例。侧边栏在单进程内只会有一个。
        /// </summary>
        internal static TaskPaneControl LastCreated { get; private set; }

        private WebView2 _webView;
        private Label _fallback;
        private HostBridge _bridge;
        private object _application;
        private string _pendingRoute;
        private bool _webViewReady;
        private PaneFocusGuard _focusGuard;

        public TaskPaneControl()
        {
            LastCreated = this;
            BuildLayout();
            // 构造期不能等待异步初始化，否则宿主 UI 线程会被卡住。
            BeginInitializeWebView();
        }

        /// <summary>
        /// 窗口句柄就绪后装上焦点守卫。
        ///
        /// 必须等到这里：守卫要按窗口树判断点击是否发生在面板内，
        /// 构造期还没有句柄可用。此时正处于宿主的 UI 线程，
        /// 线程级钩子也只有装在这个线程上才能看到网格的鼠标消息。
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            try
            {
                _focusGuard?.Dispose();
                _focusGuard = PaneFocusGuard.Install(Handle);
            }
            catch (Exception ex)
            {
                // 守卫装不上只是焦点体验退回原状，不影响面板本身。
                Log.Warn("安装焦点守卫失败：" + ex.Message);
            }
        }

        private void BuildLayout()
        {
            SuspendLayout();
            BackColor = Color.White;
            Dock = DockStyle.Fill;
            Padding = Padding.Empty;

            _fallback = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "ChatSheet 正在初始化…",
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.FromArgb(70, 70, 70),
                Visible = true,
            };
            Controls.Add(_fallback);

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Visible = false,
            };
            Controls.Add(_webView);

            ResumeLayout(false);
        }

        private void BeginInitializeWebView()
        {
            try
            {
                InitializeWebViewAsync().ContinueWith(
                    task =>
                    {
                        if (task.Exception != null)
                        {
                            ShowFallback("WebView2 初始化失败：" + Flatten(task.Exception));
                            Log.Error("WebView2 初始化失败", task.Exception);
                        }
                    },
                    TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                ShowFallback("WebView2 启动失败：" + ex.Message);
                Log.Error("WebView2 启动失败", ex);
            }
        }

        private async Task InitializeWebViewAsync()
        {
            // 用户数据目录必须放到可写位置：宿主安装目录通常不可写，
            // 默认行为会让 WebView2 在 Excel/WPS 进程内直接初始化失败。
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatSheet",
                "webview2");
            Directory.CreateDirectory(userDataFolder);

            var options = new CoreWebView2EnvironmentOptions
            {
                Language = "zh-CN",
            };

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options)
                .ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

            ConfigureWebView();
            _webViewReady = true;

            _bridge = new HostBridge(_webView.CoreWebView2, () => _application)
            {
                // 控件可能在桥创建前就已收到这两个委托，此处补齐。
                WidthAdjuster = _widthAdjuster,
                WidthPersister = _widthPersister,
            };
            _bridge.Start();

            NavigateToRoot();
            ShowWebView();
            Log.Info("WebView2 初始化成功，运行时版本 " + SafeRuntimeVersion());
        }

        private void ConfigureWebView()
        {
            var core = _webView.CoreWebView2;
            var settings = core.Settings;

            // 侧边栏是本地可信 UI，关掉浏览器化的入口，避免用户误入开发者视图或右键菜单。
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreDevToolsEnabled = IsDebugBuild();
            settings.IsSwipeNavigationEnabled = false;

            // 用虚拟主机映射直接加载本地静态文件：不起 HTTP 服务、不占端口、不需要证书。
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                WebRootPath(),
                CoreWebView2HostResourceAccessKind.Allow);

            // 外部链接交给系统浏览器，侧边栏本身不做站外导航。
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
        }

        private const string VirtualHost = "chatsheet.local";

        private static string WebRootPath()
        {
            var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            return Path.Combine(baseDir, "web");
        }

        private void NavigateToRoot()
        {
            var target = $"https://{VirtualHost}/index.html";
            if (!string.IsNullOrEmpty(_pendingRoute))
            {
                target += "#" + _pendingRoute;
                _pendingRoute = null;
            }

            _webView.CoreWebView2.Navigate(target);
        }

        /// <summary>切换面板内路由。WebView2 未就绪时先记下，初始化完成后再应用。</summary>
        internal void NavigateTo(string route)
        {
            if (string.IsNullOrWhiteSpace(route))
            {
                return;
            }

            // 与 SendChatText 同理：可能由自动化接口的调用方线程进入。
            if (InvokeRequired)
            {
                Invoke(new Action(() => NavigateTo(route)));
                return;
            }

            if (!_webViewReady)
            {
                _pendingRoute = route;
                return;
            }

            try
            {
                _bridge?.PostNavigate(route);
            }
            catch (Exception ex)
            {
                Log.Error("面板路由切换失败", ex);
            }
        }

        /// <summary>
        /// 把文本填入输入框并触发发送。
        /// 用脚本驱动真实 DOM 事件，因此走的是与用户点击完全相同的路径，
        /// 不会绕过界面逻辑而产生「测试通过但实际不可用」的假象。
        /// </summary>
        internal string SendChatText(string text)
        {
            // 自动化接口由调用方线程进入（例如脚本宿主的 COM 线程），
            // 而 WebView2 的托管包装有显式的 UI 线程检查，必须先编组。
            // 窗格等真正的 COM 对象会被自动编组回 STA，唯独 WebView2 会拦。
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => SendChatText(text)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var encoded = System.Web.HttpUtility.JavaScriptStringEncode(text ?? string.Empty);
            var script =
                "(() => {" +
                "  const box = document.getElementById('composer');" +
                "  const btn = document.getElementById('send');" +
                "  if (!box || !btn) { return '未找到输入框或发送按钮'; }" +
                $"  box.value = '{encoded}';" +
                "  box.dispatchEvent(new Event('input', { bubbles: true }));" +
                "  btn.click();" +
                "  return '已触发发送';" +
                "})()";

            try
            {
                // 不等待结果：发送会启动一轮长任务，等待它会阻塞 UI 线程。
                _webView.CoreWebView2.ExecuteScriptAsync(script);
                return "已投递";
            }
            catch (Exception ex)
            {
                return "失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 点击当前待处理的审批卡片，用于端到端验证审批链路。
        /// 点的是真实按钮，因此与手工操作走同一路径。
        /// </summary>
        internal string ClickApprovalButton(bool approve)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ClickApprovalButton(approve)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var label = approve ? "允许" : "拒绝";
            var script =
                "(() => {" +
                "  const cards = document.querySelectorAll('.approval');" +
                "  for (const card of cards) {" +
                "    if (card.querySelector('.approval-outcome')) { continue; }" +
                "    const buttons = card.querySelectorAll('.approval-actions .btn');" +
                "    for (const b of buttons) {" +
                $"      if (b.textContent === '{label}') {{ b.click(); return '已点击{label}'; }}" +
                "    }" +
                "  }" +
                "  return '无待处理卡片';" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 驱动模型/思考等级选择器，供端到端验证使用。
        /// 点击真实 DOM 元素，因此与手工操作走同一路径。
        /// </summary>
        internal string DrivePicker(string action)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => DrivePicker(action)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var encoded = System.Web.HttpUtility.JavaScriptStringEncode(action ?? string.Empty);
            var script =
                "(() => {" +
                $"  const action = '{encoded}';" +
                "  const trigger = document.getElementById('picker-trigger');" +
                "  const popup = document.getElementById('picker-pop');" +
                "  if (!trigger || !popup) { return '选择器不存在'; }" +
                "  const names = (colId) => Array.from(" +
                "    document.querySelectorAll('#' + colId + ' .picker-item-name')" +
                "  ).map((n) => n.textContent);" +
                "  if (action === 'open') { if (popup.hidden) { trigger.click(); } return '已展开'; }" +
                "  if (action === 'close') { if (!popup.hidden) { trigger.click(); } return '已收起'; }" +
                "  if (action === 'models') { return names('picker-models').join('|'); }" +
                "  if (action === 'thinkings') { return names('picker-thinkings').join('|'); }" +
                "  if (action === 'state') {" +
                "    const m = document.getElementById('picker-model');" +
                "    const t = document.getElementById('picker-thinking');" +
                "    return (m ? m.textContent : '?') + ' / ' + (t ? t.textContent : '?') +" +
                "      ' / 展开=' + (!popup.hidden);" +
                "  }" +
                "  const pick = (colId, label) => {" +
                "    const rows = Array.from(document.querySelectorAll('#' + colId + ' .picker-item'));" +
                "    for (const row of rows) {" +
                "      const name = row.querySelector('.picker-item-name');" +
                "      if (name && name.textContent === label) { row.click(); return '已选择 ' + label; }" +
                "    }" +
                "    return '未找到 ' + label + '（可选：' + names(colId).join('、') + '）';" +
                "  };" +
                "  if (action.indexOf('pick-model:') === 0) {" +
                "    return pick('picker-models', action.slice('pick-model:'.length));" +
                "  }" +
                "  if (action.indexOf('pick-thinking:') === 0) {" +
                "    return pick('picker-thinkings', action.slice('pick-thinking:'.length));" +
                "  }" +
                "  return '未知动作 ' + action;" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 附加图片，供端到端验证多模态链路。
        ///
        /// 通过构造 DataTransfer 触发真实的 drop 事件，走与用户拖入完全相同的
        /// 代码路径；直接改内部状态会绕过校验，测出来的结论不可靠。
        /// </summary>
        internal string AttachImage(string dataUrl, string name)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => AttachImage(dataUrl, name)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var encodedUrl = System.Web.HttpUtility.JavaScriptStringEncode(dataUrl ?? string.Empty);
            var encodedName = System.Web.HttpUtility.JavaScriptStringEncode(name ?? "test.png");

            // 必须全程同步：ExecuteScriptAsync 不会等待 Promise，
            // 用 async 脚本只会拿到未解析的对象。因此用 atob 同步解码，
            // 不走 fetch。
            var script =
                "(() => {" +
                "  try {" +
                $"    const url = '{encodedUrl}';" +
                "    const comma = url.indexOf(',');" +
                "    const meta = url.slice(0, comma);" +
                "    const mime = /^data:([^;]+)/.exec(meta)?.[1] ?? 'image/png';" +
                "    const bytes = atob(url.slice(comma + 1));" +
                "    const buffer = new Uint8Array(bytes.length);" +
                "    for (let i = 0; i < bytes.length; i++) { buffer[i] = bytes.charCodeAt(i); }" +
                $"    const file = new File([buffer], '{encodedName}', {{ type: mime }});" +
                "    const dt = new DataTransfer();" +
                "    dt.items.add(file);" +
                "    const zone = document.querySelector('.chat-input');" +
                "    if (!zone) { return '输入区不存在'; }" +
                "    zone.dispatchEvent(new DragEvent('drop', { dataTransfer: dt, bubbles: true, cancelable: true }));" +
                "    return '已派发 drop 事件';" +
                "  } catch (e) { return '失败：' + e.message; }" +
                "})()";

            var dispatched = RunScriptSync(script, TimeSpan.FromSeconds(5));
            if (!dispatched.StartsWith("已派发", StringComparison.Ordinal))
            {
                return dispatched;
            }

            // FileReader 是异步的，轮询等待缩略图出现。
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(120);

                var count = RunScriptSync(
                    "document.querySelectorAll('.attachment').length.toString()",
                    TimeSpan.FromSeconds(3));

                if (int.TryParse(count, out var parsed) && parsed > 0)
                {
                    return $"已附加，当前 {parsed} 张";
                }
            }

            return "已派发 drop 但未出现附件";
        }

        /// <summary>
        /// 点击第 index 个操作卡片上的撤销/恢复按钮，供端到端验证使用。
        /// 返回点击前的按钮文字，据此可判断本次执行的是撤销还是恢复。
        /// </summary>
        internal string ClickUndoButton(int index)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ClickUndoButton(index)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const buttons = document.querySelectorAll('.tool-undo');" +
                $"  const target = buttons[{index}];" +
                "  if (!target) { return '无撤销按钮（共 ' + buttons.length + ' 个）'; }" +
                "  const label = target.textContent;" +
                "  target.click();" +
                "  return label;" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 读取输入队列的当前状态，供端到端验证排队链路。
        ///
        /// 一律从 DOM 读，不去问脚本内部的队列变量：用户看到的就是 DOM，
        /// 二者若不一致，那本身就是缺陷，不该被测试掩盖。
        /// 面板每轮结束还会把内部队列长度写进布局日志，两处可交叉对账。
        ///
        /// 排队中的条目读的是输入区上方的排队条（.queue-chip），不是对话流：
        /// 排队内容在开跑前不进对话流，取消掉的也不留痕，那里只有已发生的事。
        ///
        /// 排队条最多显示三条，因此还报「可滑动」：滚动高度超过可视高度时为真，
        /// 这是从 DOM 侧确认限高确实生效、其余条目仍可滑到的唯一途径。
        ///
        /// 字段用竖线分隔而非 JSON：这个返回值要在 PowerShell 里做字符串断言，
        /// 少一层解析少一处出错的地方。
        /// </summary>
        internal string ReadQueueState()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ReadQueueState()));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const textOf = (n) => (n.querySelector('.msg-text')?.textContent ?? '').trim();" +
                "  const chipTextOf = (n) => (n.querySelector('.queue-chip-text')?.textContent ?? '').trim();" +
                "  const queued = Array.from(document.querySelectorAll('.queue-chip'));" +
                "  const sent = Array.from(document.querySelectorAll('.msg-user'));" +
                "  const strip = document.getElementById('queue-strip');" +
                "  const send = document.getElementById('send');" +
                "  const box = document.getElementById('composer');" +
                "  return [" +
                "    '排队=' + queued.length," +
                "    '已发送=' + sent.length," +
                "    '按钮=' + (send ? (send.getAttribute('aria-label') ?? '') : '无')," +
                "    '输入框可用=' + (box ? !box.disabled : false)," +
                "    '排队条可见=' + (strip ? !strip.hidden : false)," +
                "    '排队条可滑动=' + (strip ? strip.scrollHeight > strip.clientHeight + 1 : false)," +
                "    '位次=' + queued.map((n) => n.querySelector('.queue-chip-pos')?.textContent ?? '?').join('，')," +
                "    '排队内容=' + queued.map(chipTextOf).join('，')," +
                "    '已发内容=' + sent.map(textOf).join('，')," +
                "  ].join(' | ');" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 取消第 index 条排队中的输入，供端到端验证取消链路。
        /// 点的是真实按钮，与手工操作同一路径。
        /// </summary>
        internal string CancelQueued(int index)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => CancelQueued(index)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const nodes = document.querySelectorAll('.queue-chip');" +
                $"  const target = nodes[{index}];" +
                "  if (!target) { return '无排队消息（共 ' + nodes.length + ' 条）'; }" +
                "  const button = target.querySelector('.queue-chip-cancel');" +
                "  if (!button) { return '该条没有取消按钮'; }" +
                "  const text = (target.querySelector('.queue-chip-text')?.textContent ?? '').trim();" +
                "  button.click();" +
                "  return '已取消：' + text;" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 点击「适配」浮层里的某个对齐选项，等同于用户悬停展开后点选。
        ///
        /// alignment 取 left/center/right。走真实点击而非直接调通道：
        /// 用户报的缺陷恰恰出在按钮到撤销入口这一段，绕过界面就测不到。
        /// </summary>
        internal string ClickFit(string alignment)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ClickFit(alignment)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var encoded = System.Web.HttpUtility.JavaScriptStringEncode(alignment ?? "center");
            var script =
                "(() => {" +
                $"  const want = '{encoded}';" +
                "  const item = document.querySelector('.fit-item[data-align=\"' + want + '\"]');" +
                "  if (!item) { return '未找到对齐选项 ' + want; }" +
                "  item.click();" +
                "  return '已点击 ' + want;" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 读取最后一张工具操作卡片：名称、来源、状态与撤销入口。
        ///
        /// 面板直接发起的操作（适配）与模型发起的用同一种卡片，只在来源上区分，
        /// 因此「来源」这个字段是这两类操作在 DOM 上唯一稳定的分辨依据——
        /// 边条颜色是 CSS 的事，断言颜色只会把样式微调也变成测试失败。
        /// </summary>
        internal string ReadLastToolCard()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ReadLastToolCard()));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const list = document.querySelectorAll('.tool-card');" +
                "  const last = list[list.length - 1];" +
                "  if (!last) { return '无操作卡片'; }" +
                "  const textOf = (sel) => (last.querySelector(sel)?.textContent ?? '').trim();" +
                "  const button = last.querySelector('.tool-undo');" +
                "  return [" +
                "    '名称=' + textOf('.tool-name')," +
                "    '来源=' + (last.classList.contains('is-manual') ? '手动' : '模型')," +
                "    '标记=' + (textOf('.tool-origin') || '无')," +
                "    '状态=' + textOf('.tool-state')," +
                "    '撤销入口=' + (button ? button.textContent : '无')," +
                "    '卡片数=' + list.length," +
                "  ].join(' | ');" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>读取最后一条提示胶囊的文字与它是否带撤销入口。</summary>
        internal string ReadLastNotice()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ReadLastNotice()));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const list = document.querySelectorAll('.notice');" +
                "  const last = list[list.length - 1];" +
                "  if (!last) { return '无提示'; }" +
                "  const button = last.querySelector('.tool-undo');" +
                "  return [" +
                "    '文字=' + last.textContent.replace(/撤销|恢复/g, '').trim()," +
                "    '撤销入口=' + (button ? button.textContent : '无')," +
                "  ].join(' | ');" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 点击发送按钮本身，不预先填入文本。
        ///
        /// 与 SendChatText 分开是必要的：输入框为空且正在处理时，
        /// 该按钮的含义是「停止」，而 SendChatText 总会先填字，
        /// 那样永远走不到停止这条路径。
        /// </summary>
        internal string ClickSendButton()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ClickSendButton()));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const button = document.getElementById('send');" +
                "  if (!button) { return '未找到发送按钮'; }" +
                "  const label = button.getAttribute('aria-label') ?? '';" +
                "  button.click();" +
                "  return '已点击：' + label;" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 读取面板中指定元素的文本，供端到端验证界面内容。
        /// </summary>
        internal string ReadElementText(string elementId)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ReadElementText(elementId)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var encoded = System.Web.HttpUtility.JavaScriptStringEncode(elementId ?? string.Empty);
            var script =
                "(() => {" +
                $"  const node = document.getElementById('{encoded}');" +
                "  if (!node) { return '<元素不存在>'; }" +
                // 折叠多余空白，便于在日志与断言中比对。
                "  return (node.textContent || '').replace(/\\s+/g, ' ').trim();" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 读取输入框内容与选中范围，供验证键盘输入是否真的进了面板。
        /// 返回 value|选中起-选中止。
        /// </summary>
        internal string ReadComposerText()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(ReadComposerText));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const box = document.getElementById('composer');" +
                "  if (!box) { return '<无输入框>'; }" +
                "  return box.value + '|' + box.selectionStart + '-' + box.selectionEnd;" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 同步执行脚本并取回结果。
        ///
        /// 只用于自动化验证：需要同步答案（是否找到卡片），而
        /// ExecuteScriptAsync 是异步的。这里用有界的消息泵等待，
        /// 超时即返回，不会永久占住 UI 线程。
        /// 生产代码路径一律用异步，不走这里。
        /// </summary>
        private string RunScriptSync(string script, TimeSpan timeout)
        {
            string result = null;
            var completed = false;

            var task = _webView.CoreWebView2.ExecuteScriptAsync(script);
            task.ContinueWith(
                t =>
                {
                    result = t.Exception != null ? "脚本失败：" + t.Exception.GetBaseException().Message : t.Result;
                    completed = true;
                },
                TaskScheduler.FromCurrentSynchronizationContext());

            var deadline = DateTime.UtcNow + timeout;
            while (!completed && DateTime.UtcNow < deadline)
            {
                // 必须抽送消息：脚本结果的回调依赖 UI 线程的消息循环。
                Application.DoEvents();
                System.Threading.Thread.Sleep(30);
            }

            if (!completed)
            {
                return "等待脚本结果超时";
            }

            // ExecuteScriptAsync 返回的是 JSON 字面量，字符串会带引号。
            return result?.Trim('"') ?? string.Empty;
        }

        /// <summary>绑定宿主 Application 对象，供工具层访问工作簿。</summary>
        internal void Attach(object application)
        {
            _application = application;
        }

        /// <summary>
        /// 注入宽度校准与存档能力。窗格对象由控制器持有，控件本身拿不到，
        /// 因此以委托形式传入，再转交消息桥供面板使用。
        /// </summary>
        internal void AttachWidthHandlers(Func<int, int, double, int> adjuster, Func<int> persister)
        {
            _widthAdjuster = adjuster;
            _widthPersister = persister;
            if (_bridge != null)
            {
                _bridge.WidthAdjuster = adjuster;
                _bridge.WidthPersister = persister;
            }
        }

        private Func<int, int, double, int> _widthAdjuster;

        private Func<int> _widthPersister;

        private void ShowWebView()
        {
            if (_fallback != null)
            {
                _fallback.Visible = false;
            }

            if (_webView != null)
            {
                _webView.Visible = true;
            }
        }

        private void ShowFallback(string message)
        {
            try
            {
                if (_webView != null)
                {
                    _webView.Visible = false;
                }

                if (_fallback != null)
                {
                    _fallback.Visible = true;
                    _fallback.Text = message + Environment.NewLine + Environment.NewLine +
                        "日志：" + Log.CurrentPath;
                }
            }
            catch
            {
            }
        }

        private static bool IsDebugBuild()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        private string SafeRuntimeVersion()
        {
            try
            {
                return _webView?.CoreWebView2?.Environment?.BrowserVersionString ?? "未知";
            }
            catch
            {
                return "未知";
            }
        }

        private static void OpenExternal(string uri)
        {
            try
            {
                if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Process.Start(uri);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("打开外部链接失败：" + ex.Message);
            }
        }

        private static string Flatten(Exception ex)
        {
            var aggregate = ex as AggregateException;
            var target = aggregate?.Flatten().InnerExceptions.Count > 0
                ? aggregate.Flatten().InnerExceptions[0]
                : ex;
            return target.Message;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    // 钩子必须先卸：留在宿主线程上的钩子会持续收到消息，
                    // 而它引用的窗口句柄此刻已经失效。
                    _focusGuard?.Dispose();
                    _bridge?.Dispose();
                    _webView?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("释放面板资源失败：" + ex.Message);
                }
                finally
                {
                    _focusGuard = null;
                    _bridge = null;
                    _webView = null;
                    _application = null;
                    if (ReferenceEquals(LastCreated, this))
                    {
                        LastCreated = null;
                    }
                }
            }

            base.Dispose(disposing);
        }
    }
}
