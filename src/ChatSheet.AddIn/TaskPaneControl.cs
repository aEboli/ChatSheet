using System;
using System.Collections.Generic;
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
            // 用上次记住的主题，而不是写死白色。这一步发生在 WebView2 初始化之前，
            // 深色主题下写死白色就会先闪一块白，页面画出来之后才变深。
            _theme = LoadStoredTheme();
            BackColor = PaneBackColor(_theme);
            Dock = DockStyle.Fill;
            Padding = Padding.Empty;

            _fallback = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "ChatSheet 正在初始化…",
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = PaneForeColor(_theme),
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

            // 页面还没画出来这段时间露出的是这个底色。跟着已记住的主题走，
            // 否则深色下从导航到首屏之间会闪一块白。
            _webView.DefaultBackgroundColor = PaneBackColor(_theme);

            _bridge = new HostBridge(_webView.CoreWebView2, () => _application)
            {
                // 控件可能在桥创建前就已收到这两个委托，此处补齐。
                WidthAdjuster = _widthAdjuster,
                WidthPersister = _widthPersister,
                ThemeApplier = ApplyTheme,
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
        /// <summary>
        /// 把一张审批卡投进页面并量它的排版。仅供 PaneHarness 使用。
        ///
        /// 为什么要走真实 WebView2：对照表在窄栏（300-480px）下够不够读，
        /// 只有真实渲染器算得出来——列宽由 table-layout 与内容共同决定，
        /// 折行到第几行由 line-clamp 决定，这些在假 DOM 里全都量不到。
        /// 投的是一条宿主推送，驱动的是 addApprovalCard 本身，不复刻它。
        /// </summary>
        internal string DriveApproval(string action)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => DriveApproval(action)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var encoded = System.Web.HttpUtility.JavaScriptStringEncode(action ?? string.Empty);
            var script =
                "(() => {" +
                $"  const action = '{encoded}';" +
                "  const post = (payload) => window.dispatchEvent(" +
                "    new MessageEvent('message', { data: payload }));" +
                // 量排版：表宽、各列宽、有没有横向溢出、行数。
                "  if (action === 'measure') {" +
                "    const card = document.querySelector('.approval');" +
                "    if (!card) { return '没有审批卡'; }" +
                "    const table = card.querySelector('.approval-preview-table');" +
                "    if (!table) { return '没有对照表'; }" +
                "    const rows = table.querySelectorAll('tr');" +
                "    const firstData = rows[1];" +
                "    const tds = firstData ? firstData.querySelectorAll('td') : [];" +
                "    const w = (n) => n ? Math.round(n.getBoundingClientRect().width) : 0;" +
                "    const actions = card.querySelector('.approval-actions');" +
                "    const cardBox = card.getBoundingClientRect();" +
                "    const tableBox = table.getBoundingClientRect();" +
                "    return '视口=' + window.innerWidth +" +
                "      ' 卡宽=' + Math.round(cardBox.width) +" +
                "      ' 表宽=' + Math.round(tableBox.width) +" +
                "      ' 列宽=' + Array.from(tds).map(w).join('/') +" +
                "      ' 行数=' + rows.length +" +
                "      ' 表溢出=' + (tableBox.right > cardBox.right + 1) +" +
                "      ' 按钮可见=' + (actions ? actions.getBoundingClientRect().height > 0 : false) +" +
                "      ' 值列换行=' + (tds[1] ? Math.round(tds[1].getBoundingClientRect().height) : 0);" +
                "  }" +
                "  return '未知动作：' + action;" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 投一条审批推送给面板，驱动真实的 addApprovalCard。
        ///
        /// 值里刻意放长日期、空原值和一条 51 字符的公式：窄栏下要量的正是
        /// 这三种内容会不会被截到读不出来。走 HostBridge 的正式出口，
        /// 不在注入脚本里复刻卡片——复刻件与真件迟早漂移。
        /// </summary>
        internal void SeedApprovalForTest()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(SeedApprovalForTest));
                return;
            }

            var cells = new List<object>();
            for (var r = 1; r <= 3; r++)
            {
                cells.Add(new
                {
                    row = r,
                    column = 1,
                    before = $"2026-09-0{r} 08:30:00",
                    after = $"2026-09-0{r}",
                    beforeEmpty = false,
                    afterEmpty = false,
                });
                cells.Add(new
                {
                    row = r,
                    column = 2,
                    before = string.Empty,
                    after = $"=IFERROR(VLOOKUP($A{r},Sheet2!$A:$D,4,FALSE),\"未找到\")",
                    beforeEmpty = true,
                    afterEmpty = false,
                });
            }

            _bridge?.PostRaw(new
            {
                kind = "approval-request",
                id = "harness-1",
                tool = "write_values",
                risk = "Write",
                impact = string.Empty,
                impactRange = new { sheet = "Sheet1", address = "$A$1:$B$3", cells = 6 },
                preview = new
                {
                    currentUnreadable = false,
                    formattingMixed = false,
                    omittedCells = 12,
                    discardedValues = 0,
                    kind = "write",
                    cells,
                },
                args = new { range = "$A$1:$B$3" },
            });
        }

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
                // 常用名单：拨开关、标星、读筛选后的状态。
                // 真实宿主里才能看出筛选生效后还能不能反复切换模型。
                "  if (action === 'toggle-only-favorites') {" +
                "    const b = document.getElementById('picker-only-favorites');" +
                "    if (!b) { return '开关不存在'; }" +
                "    b.click();" +
                "    return '已拨动，当前=' + b.getAttribute('aria-pressed');" +
                "  }" +
                "  if (action === 'favorites') {" +
                "    const stars = Array.from(document.querySelectorAll('#picker-models .picker-star'));" +
                "    const on = stars.filter((s) => s.getAttribute('aria-pressed') === 'true').length;" +
                "    const b = document.getElementById('picker-only-favorites');" +
                "    const hidden = document.querySelector('#picker-models .picker-hidden-count');" +
                "    return '星标=' + on + '/' + stars.length +" +
                "      ' 开关=' + (b ? b.getAttribute('aria-pressed') : '?') +" +
                "      ' 收起说明=' + (hidden ? hidden.textContent : '无');" +
                "  }" +
                // 按需确认：点某一行的「试一下」，以及读回该行的状态。
                "  if (action.indexOf('probe:') === 0) {" +
                "    const label = action.slice('probe:'.length);" +
                "    const rows = Array.from(document.querySelectorAll('#picker-models .picker-row'));" +
                "    for (const row of rows) {" +
                "      const name = row.querySelector('.picker-item-name');" +
                "      const button = row.querySelector('.picker-probe');" +
                "      if (name && name.textContent === label) {" +
                "        if (!button) { return '该行没有「试一下」（已有判定）'; }" +
                "        button.click();" +
                "        return '已点击 ' + label;" +
                "      }" +
                "    }" +
                "    return '未找到 ' + label;" +
                "  }" +
                // 一行一个字段，键名前缀固定。判定的结论已从行上的文字改为
                // 行的颜色，因此这里除状态点外还要报出行上真正生效的标记 class
                // 与悬停说明——不然「模型名到底有没有变红」在宿主里无从断言，
                // 而那正是这次改动的全部意图。
                //
                // 状态用 status=<值> 的形式报，不裸报值：「可用」是「不可用」的
                // 子串，裸报时断言一个模型可用会在它其实不可用时照样通过。
                "  if (action.indexOf('verdict:') === 0) {" +
                "    const label = action.slice('verdict:'.length);" +
                "    const rows = Array.from(document.querySelectorAll('#picker-models .picker-row'));" +
                "    for (const row of rows) {" +
                "      const name = row.querySelector('.picker-item-name');" +
                "      if (!name || name.textContent !== label) { continue; }" +
                "      const item = row.querySelector('.picker-item');" +
                "      const dot = row.querySelector('.picker-availability-dot');" +
                "      const cls = dot ? dot.className : '无点';" +
                "      const itemCls = item ? item.className : '';" +
                "      let state = '未确认';" +
                "      if (cls.indexOf('is-probing') >= 0) { state = '正在确认'; }" +
                "      else if (cls.indexOf('is-ok') >= 0) { state = '可用'; }" +
                "      else if (cls.indexOf('is-error') >= 0) { state = '不可用'; }" +
                "      let mark = '无';" +
                "      if (itemCls.indexOf('is-unavailable') >= 0) { mark = '红字'; }" +
                "      else if (itemCls.indexOf('is-available') >= 0) { mark = '可用标记'; }" +
                "      else if (itemCls.indexOf('is-probing') >= 0) { mark = '确认中'; }" +
                "      const hint = row.querySelector('.picker-item-hint');" +
                // 换行报成 ASCII 标记，不折叠空白：前导换行、空行这类问题一旦被
                // replace(/\s+/g,' ') 折掉就再也看不见，而它正是用户看到的东西。
                // 用 <NL> 而不是 JSON 转义——控制台代码页会把中文搞坏，
                // 但 ASCII 标记与行数统计一定读得出来。
                "      const rawTitle = item && item.title ? item.title : '';" +
                "      const title = '[' + rawTitle.split('\\n').length + '行]' +" +
                "        rawTitle.replace(/\\n/g, '<NL>');" +
                "      return '状态=' + state +" +
                "        ' | 标记=' + mark +" +
                "        ' | 行内=' + (hint ? hint.textContent : '无') +" +
                "        ' | 悬停=' + (title || '无') +" +
                "        ' | 有试一下=' + (row.querySelector('.picker-probe') !== null);" +
                "    }" +
                "    return '未找到 ' + label;" +
                "  }" +
                // 造一份三态齐全的模型列表，供在真实渲染器里核对判定的显示。
                //
                // 为什么需要它：三态里的「不可用」要靠服务端点名模型才会得出，
                // 真实地拿到它需要一个会那样报错的网关。而这次改动的全部意图是
                // 「不可用要一眼看得出来」，那句话只有算出来的颜色能证实——
                // 假 DOM 里没有计算样式，CSS 静态检查也看不出 var() 是否取到了值。
                //
                // 直接动态 import 面板自己的模块：同一个 URL 的重复 import 返回
                // 同一个模块实例，因此这里调的 syncPicker 就是页面正在用的那一个，
                // 不是另一份副本。异步结果存到 window 上，由 seed-state 读回。
                "  if (action === 'seed-demo') {" +
                "    window.__seedResult = '正在注入…';" +
                "    (async () => {" +
                "      try {" +
                "        const picker = await import('./scripts/picker.js');" +
                "        const catalog = await import('./scripts/model-catalog.js');" +
                "        const conn = {" +
                "          mode: 'CustomApi'," +
                "          customProtocol: 'openai-chat-completions'," +
                "          customBaseUrl: 'https://seed.example.test/v1'," +
                "        };" +
                // 目录里放一个真实长度的 ID：网关的 ID 动辄四十来字符，而短名
                // （seed-ok 之类）永远试不出截断。浮层宽度按内容取，这一行就是
                // 「够不够宽」的判据。
                "        catalog.putModelCatalog(conn, ['seed-ok', 'seed-bad', 'seed-unknown'," +
                "          'deepseek/deepseek-v4-flash-vision-preview']);" +
                "        picker.syncPicker({" +
                "          ...conn," +
                "          model: 'seed-ok'," +
                "          thinking: 'High'," +
                "          thinkingSupported: ['Off', 'Minimal', 'Low', 'Medium', 'High']," +
                "          favorites: []," +
                "          availability: { 'seed-ok': 'Available', 'seed-bad': 'Unavailable' }," +
                "          onlyFavoriteModels: false," +
                "        });" +
                "        window.__seedResult = '已注入';" +
                "      } catch (e) { window.__seedResult = '注入失败：' + e.message; }" +
                "    })();" +
                "    return '已开始注入';" +
                "  }" +
                "  if (action === 'seed-state') { return window.__seedResult || '未注入'; }" +
                // 批量测试正测到某个模型时，那一行要被标记且扫光真的在跑。
                //
                // 走真实推送路径：`probe-progress` 是加载项在批量测试里逐个推的消息，
                // picker.js 订阅它、置批量进度并重渲列表。不在这里复刻那套渲染——
                // 复刻件会与实现漂移，那时测的是复刻件而不是面板。
                //
                // 不带 verdict：带了会顺手把判定记下来，污染同一次运行里后面几条
                // 关于三态颜色的断言。
                "  if (action.indexOf('bulk-testing:') === 0) {" +
                "    const id = action.slice('bulk-testing:'.length);" +
                "    if (!window.chrome || !window.chrome.webview) { return '不在宿主内'; }" +
                "    window.chrome.webview.dispatchEvent(new MessageEvent('message', {" +
                "      data: { kind: 'probe-progress', index: 1, total: 3, model: id }," +
                "    }));" +
                "    return '已推送 ' + id;" +
                "  }" +
                // 收尾：批量结束。必须显式推 done——推一个空的 model 只会把进度
                // 留在「进行中」，那时列头按钮仍是「停止」，后面关于排版与三态的
                // 断言会读到一个不该有的状态。
                "  if (action === 'bulk-done') {" +
                "    if (!window.chrome || !window.chrome.webview) { return '不在宿主内'; }" +
                "    window.chrome.webview.dispatchEvent(new MessageEvent('message', {" +
                "      data: { kind: 'probe-progress', done: true }," +
                "    }));" +
                "    return '已结束批量';" +
                "  }" +
                // 读某一行的扫光状态。
                //
                // 动画挂在 ::after 上，element.getAnimations() 默认不含伪元素，
                // 要 subtree: true 才拿得到——漏了这个参数会读到「没有动画」，
                // 而那与「扫光根本没接上」在结果里长得一模一样。
                "  if (action.indexOf('sweep:') === 0) {" +
                "    const label = action.slice('sweep:'.length);" +
                "    const rows = Array.from(document.querySelectorAll('#picker-models .picker-row'));" +
                "    for (const row of rows) {" +
                "      const name = row.querySelector('.picker-item-name');" +
                "      if (!name || name.textContent !== label) { continue; }" +
                "      const item = row.querySelector('.picker-item');" +
                "      if (!item) { return '该行没有 .picker-item'; }" +
                "      const list = item.getAnimations ? item.getAnimations({ subtree: true }) : [];" +
                "      const sweeps = list.filter((a) => a.animationName === 'model-test-sweep');" +
                "      const style = getComputedStyle(item, '::after');" +
                "      return '标记=' + item.classList.contains('is-testing') +" +
                "        ' | 动画数=' + sweeps.length +" +
                "        ' | 伪元素=' + (sweeps[0] && sweeps[0].effect" +
                "            ? (sweeps[0].effect.pseudoElement || '无') : '无') +" +
                "        ' | 在跑=' + (sweeps[0] ? sweeps[0].playState : '无') +" +
                "        ' | 底色=' + style.backgroundImage.slice(0, 60) +" +
                "        ' | 裁剪=' + getComputedStyle(item).overflow +" +
                "        ' | 吃点击=' + style.pointerEvents;" +
                "    }" +
                "    return '未找到 ' + label;" +
                "  }" +
                // 读一行模型名算出来的颜色，以及行的几何。颜色是这次改动的核心断言：
                // class 在、规则在，但变量名写错时浏览器会静默退回默认色。
                "  if (action.indexOf('name-color:') === 0) {" +
                "    const label = action.slice('name-color:'.length);" +
                "    const names = Array.from(document.querySelectorAll('#picker-models .picker-item-name'));" +
                "    for (const name of names) {" +
                "      if (name.textContent !== label) { continue; }" +
                "      const item = name.closest('.picker-item');" +
                "      const dot = item?.parentElement?.querySelector('.picker-availability-dot');" +
                "      const rect = item ? item.getBoundingClientRect() : { height: 0, width: 0 };" +
                "      return '色=' + getComputedStyle(name).color +" +
                "        ' | 点色=' + (dot ? getComputedStyle(dot).backgroundColor : '无')+" +
                "        ' | 行class=' + (item ? item.className : '无') +" +
                "        ' | 高=' + Math.round(rect.height) +" +
                "        ' | 宽=' + Math.round(rect.width);" +
                "    }" +
                "    return '未找到 ' + label;" +
                "  }" +
                // 浮层的几何：是否超出视口顶端。浮层向上弹，超出的部分会被静默裁掉，
                // 而 overflow: hidden 让这件事连滚动条都不留。
                "  if (action === 'pop-geometry') {" +
                "    const popup = document.getElementById('picker-pop');" +
                "    if (!popup || popup.hidden) { return '浮层未展开'; }" +
                "    const r = popup.getBoundingClientRect();" +
                "    const models = document.getElementById('picker-models');" +
                "    const thinkings = document.getElementById('picker-thinkings');" +
                "    const mr = models ? models.getBoundingClientRect() : { height: 0 };" +
                "    const tr = thinkings ? thinkings.getBoundingClientRect() : { height: 0 };" +
                "    return '顶=' + Math.round(r.top) +" +
                "      ' | 底=' + Math.round(r.bottom) +" +
                "      ' | 左=' + Math.round(r.left) +" +
                "      ' | 右=' + Math.round(r.right) +" +
                "      ' | 高=' + Math.round(r.height) +" +
                "      ' | 宽=' + Math.round(r.width) +" +
                "      ' | 视口高=' + window.innerHeight +" +
                "      ' | 视口宽=' + window.innerWidth +" +
                "      ' | 出界=' + (r.top < 0) +" +
                // 横向出界单独报：浮层有 min-width，窄栏下它会赢过 max-width，
                // 而超出的部分在 body 上不产生滚动条，是静默裁掉的。
                "      ' | 右出界=' + (r.right > window.innerWidth + 1) +" +
                "      ' | 模型段高=' + Math.round(mr.height) +" +
                "      ' | 档位段高=' + Math.round(tr.height) +" +
                "      ' | 模型段可滑=' + (models ? models.scrollHeight > models.clientHeight + 1 : false);" +
                "  }" +
                // 手填一个模型 ID。派发真实的 submit 事件，与用户在输入框里
                // 按 Enter 走同一条路径——直接改内部状态会绕过并入列表那一步，
                // 而那一步恰恰是这个入口存在的理由。
                "  if (action.indexOf('manual:') === 0) {" +
                "    const id = action.slice('manual:'.length);" +
                "    const form = document.getElementById('picker-manual');" +
                "    const input = document.getElementById('picker-manual-input');" +
                "    if (!form || !input) { return '手填入口不存在'; }" +
                "    input.value = id;" +
                "    input.dispatchEvent(new Event('input', { bubbles: true }));" +
                "    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));" +
                "    return '已手填 ' + id;" +
                "  }" +
                // 「试一下」平时该是看不见的。断言 class 存在证明不了这一点——
                // 它一直在 DOM 里，藏起来靠的是算出来的 opacity。只有计算值能
                // 说明规则真的生效：选择器写错、变量取不到值，两种情况下
                // class 都还在，按钮却常显。
                "  if (action.indexOf('probe-visible:') === 0) {" +
                "    const label = action.slice('probe-visible:'.length);" +
                "    const rows = Array.from(document.querySelectorAll('#picker-models .picker-row'));" +
                "    for (const row of rows) {" +
                "      const name = row.querySelector('.picker-item-name');" +
                "      if (!name || name.textContent !== label) { continue; }" +
                "      const probe = row.querySelector('.picker-probe');" +
                "      if (!probe) { return '该行没有「试一下」'; }" +
                "      const style = getComputedStyle(probe);" +
                // 是否正被悬停：真实鼠标可能恰好停在这一行上（窗口居中弹出时常有），
                // 那时「试一下」按设计就是显形的。不报这一项的话，断言会随鼠标位置
                // 时红时绿——那种失败最耗时间，因为它看起来像代码问题。
                "      let hovered = false;" +
                "      try { hovered = row.matches(':hover') || probe.matches(':hover'); }" +
                "      catch (e) { hovered = false; }" +
                "      return '透明度=' + style.opacity +" +
                "        ' | 可点=' + (style.pointerEvents !== 'none') +" +
                "        ' | 被悬停=' + hovered +" +
                "        ' | 在DOM=true';" +
                "    }" +
                "    return '未找到 ' + label;" +
                "  }" +
                // 列头是否折成两行、模型名是否被截断。两件事都只有排版后才知道：
                // 列头元素的 offsetTop 相同即单行；模型名的 scrollWidth > clientWidth
                // 即已截断（省略号生效）。断言 CSS 文本证明不了任何一件。
                // 各元素的左右边界，用来找出哪里没对齐。
                // 肉眼说「没对齐」时，究竟是列头与行没对齐、两列列头彼此没对齐、
                // 还是行内的名字与状态点没对齐，只有量出来才分得清。
                "  if (action === 'align-geometry') {" +
                "    const box = (sel, root) => {" +
                "      const n = (root || document).querySelector(sel);" +
                "      if (!n) { return null; }" +
                "      const r = n.getBoundingClientRect();" +
                "      return { l: Math.round(r.left), r: Math.round(r.right), t: Math.round(r.top) };" +
                "    };" +
                "    const pop = document.getElementById('picker-pop');" +
                "    if (!pop || pop.hidden) { return '浮层未展开'; }" +
                "    const pr = pop.getBoundingClientRect();" +
                "    const modelsCol = document.getElementById('picker-models')?.parentElement;" +
                "    const thinkCol = document.getElementById('picker-thinkings')?.parentElement;" +
                "    const mHead = modelsCol?.querySelector('.picker-col-head span');" +
                "    const tHead = thinkCol?.querySelector('.picker-col-head span');" +
                "    const firstDot = document.querySelector('#picker-models .picker-availability-dot');" +
                "    const firstName = document.querySelector('#picker-models .picker-item-name');" +
                "    const firstThink = document.querySelector('#picker-thinkings .picker-item-name');" +
                "    const parts = [];" +
                "    const rel = (label, node) => {" +
                "      if (!node) { parts.push(label + '=无'); return; }" +
                "      const r = node.getBoundingClientRect();" +
                "      parts.push(label + '=' + Math.round(r.left - pr.left) +" +
                "        '..' + Math.round(r.right - pr.left));" +
                "    };" +
                "    rel('模型列', modelsCol);" +
                "    rel('档位列', thinkCol);" +
                "    rel('模型列头字', mHead);" +
                "    rel('档位列头字', tHead);" +
                "    rel('首个状态点', firstDot);" +
                "    rel('首个模型名', firstName);" +
                "    rel('首个档位名', firstThink);" +
                // 档位行本身的框，以及它算出来的 justify-content。
                // 名字没居中时要分清是「规则没生效」还是「行本身没占满列」。
                "    const thinkRow = document.querySelector('#picker-thinkings .picker-item');" +
                "    rel('首个档位行', thinkRow);" +
                "    if (thinkRow) {" +
                "      const cs = getComputedStyle(thinkRow);" +
                "      parts.push('档位行justify=' + cs.justifyContent);" +
                "      parts.push('档位行padding=' + cs.paddingLeft + '/' + cs.paddingRight);" +
                "      parts.push('档位行width=' + cs.width);" +
                "    }" +
                "    const thinkList = document.getElementById('picker-thinkings');" +
                "    rel('档位列表', thinkList);" +
                "    return parts.join(' | ');" +
                "  }" +
                "  if (action === 'head-geometry') {" +
                "    const head = document.querySelector('#picker-models')" +
                "      ?.parentElement?.querySelector('.picker-col-head');" +
                "    if (!head) { return '列头不存在'; }" +
                "    const kids = Array.from(head.children);" +
                // 按纵向中心分行，不按 top：列头里既有 span（约 15px 高）也有按钮
                // （18px），align-items: center 下二者 top 本就不同，用 top 去数行数
                // 会把「同一行、高度不同」误报成两行。中心相差在半行以内即同一行。
                "    const mids = kids.map((k) => {" +
                "      const r = k.getBoundingClientRect();" +
                "      return r.top + r.height / 2;" +
                "    });" +
                "    const rows = [];" +
                "    for (const m of mids) {" +
                "      if (!rows.some((r) => Math.abs(r - m) < 8)) { rows.push(m); }" +
                "    }" +
                "    const tops = { size: rows.length };" +
                "    const hr = head.getBoundingClientRect();" +
                "    const names = Array.from(" +
                "      document.querySelectorAll('#picker-models .picker-item-name'));" +
                "    const clipped = names.filter((n) => n.scrollWidth > n.clientWidth + 1).length;" +
                // 差多少像素才装得下最长那个名字。有了这个数才能说清「面板要多宽」，
                // 而不是只报「被截断了」。
                "    const shortfall = Math.max(0, ...names.map((n) => n.scrollWidth - n.clientWidth));" +
                "    const widest = Math.max(0, ...names.map((n) => n.scrollWidth));" +
                "    const lines = names.map((n) => Math.round(n.getBoundingClientRect().height));" +
                // 是否横向溢出。注意 scrollWidth 只在「没折行」时才等于单行所需宽度：
                // 一旦折了行，内容就不再溢出，scrollWidth 会等于 clientWidth——
                // 那时用它去问「装不装得下」永远得到「装得下」。
                "    const overflow = head.scrollWidth > head.clientWidth + 1;" +
                // 因此另算一份真实的单行所需宽度：子元素宽度之和 + 间隙 + 左右内边距。
                // 这个值与是否已折行无关，才能用来判断「本该单行却折了」。
                "    const cs = getComputedStyle(head);" +
                "    const gap = parseFloat(cs.columnGap || cs.gap || '0') || 0;" +
                "    const padX = (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0);" +
                "    const sumKids = kids.reduce((a, k) => a + k.getBoundingClientRect().width, 0);" +
                "    const needOneLine = Math.ceil(" +
                "      sumKids + gap * Math.max(0, kids.length - 1) + padX);" +
                "    return '列头行数=' + tops.size +" +
                "      ' | 列头高=' + Math.round(hr.height) +" +
                "      ' | 列头溢出=' + overflow +" +
                "      ' | 需要宽=' + needOneLine + '/' + head.clientWidth +" +
                "      ' | 列头元素=' + kids.length +" +
                "      ' | 元素文字=' + kids.map((k) => (k.textContent || '').trim()).join('/') +" +
                "      ' | 模型名数=' + names.length +" +
                "      ' | 被截断=' + clipped +" +
                "      ' | 还差=' + shortfall +" +
                "      ' | 最宽名=' + widest +" +
                "      ' | 名字高=' + lines.join(',');" +
                "  }" +
                // 档位行：说明文字应当收进悬停提示，行上只留档位名与可能的降级标注。
                "  if (action.indexOf('thinking-row:') === 0) {" +
                "    const label = action.slice('thinking-row:'.length);" +
                "    const rows = Array.from(document.querySelectorAll('#picker-thinkings .picker-item'));" +
                "    for (const row of rows) {" +
                "      const name = row.querySelector('.picker-item-name');" +
                "      if (!name || name.textContent !== label) { continue; }" +
                "      const hint = row.querySelector('.picker-item-hint');" +
                "      const tag = row.querySelector('.picker-thinking-tag');" +
                "      const rect = row.getBoundingClientRect();" +
                "      return '行内说明=' + (hint ? hint.textContent : '无') +" +
                "        ' | 降级标注=' + (tag ? tag.textContent : '无') +" +
                "        ' | 悬停=' + (row.title || '无').replace(/\\s+/g, ' ') +" +
                "        ' | 高=' + Math.round(rect.height) +" +
                "        ' | 宽=' + Math.round(rect.width);" +
                "    }" +
                "    return '未找到 ' + label;" +
                "  }" +
                "  if (action.indexOf('star:') === 0) {" +
                "    const label = action.slice('star:'.length);" +
                "    const rows = Array.from(document.querySelectorAll('#picker-models .picker-row'));" +
                "    for (const row of rows) {" +
                "      const name = row.querySelector('.picker-item-name');" +
                "      const star = row.querySelector('.picker-star');" +
                "      if (name && star && name.textContent === label) {" +
                "        star.click();" +
                "        return '已标星 ' + label;" +
                "      }" +
                "    }" +
                "    return '未找到 ' + label;" +
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
        /// 点主题切换按钮，返回切换后的主题。
        /// 走的是真实点击路径，与用户操作完全一致。
        /// </summary>
        internal string ClickThemeToggle()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(ClickThemeToggle));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const button = document.getElementById('theme-toggle');" +
                "  if (!button) { return '未找到主题切换按钮'; }" +
                "  button.click();" +
                "  return document.documentElement.dataset.theme || '<未设置>';" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 读取当前主题与几处关键元素的实际计算颜色。
        ///
        /// 断言计算值而不是断言 CSS 文本：变量取不到值时 var() 会静默退回
        /// 浏览器默认色，样式表里那行 var(--x) 看着完全正常，只有算出来的
        /// 颜色能暴露这种情况。返回
        /// theme=…|body=…|bar=…|composer=…|send=…|toggle=…
        /// </summary>
        internal string ReadThemeState()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(ReadThemeState));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const bg = (sel) => {" +
                "    const node = document.querySelector(sel);" +
                "    if (!node) { return '<无>'; }" +
                "    return getComputedStyle(node).backgroundColor;" +
                "  };" +
                "  const theme = document.documentElement.dataset.theme || '<未设置>';" +
                "  const scheme = getComputedStyle(document.documentElement).colorScheme;" +
                // 存了什么、以及 WebView2 报的系统偏好。
                // 两者一起才能解释「为什么是这套主题」：没存过就该跟随系统，
                // 而 WebView2 里的 prefers-color-scheme 未必等于 Windows 的设置。
                "  let stored = '<读不到>';" +
                "  try { stored = window.localStorage.getItem('chatsheet.theme') || '<未存过>'; }" +
                "  catch (e) { stored = '<localStorage 不可用>'; }" +
                "  const sysDark = window.matchMedia" +
                "    ? window.matchMedia('(prefers-color-scheme: dark)').matches" +
                "    : '<无 matchMedia>';" +
                "  const text = getComputedStyle(document.body).color;" +
                // 太阳与月亮同时只能显示一个。
                "  const shown = ['.theme-glyph-sun', '.theme-glyph-moon']" +
                "    .filter((s) => {" +
                "      const n = document.querySelector(s);" +
                "      return n && getComputedStyle(n).display !== 'none';" +
                "    })" +
                "    .map((s) => s.replace('.theme-glyph-', ''));" +
                "  return [" +
                "    'theme=' + theme," +
                "    'stored=' + stored," +
                "    'sysDark=' + sysDark," +
                "    'scheme=' + scheme," +
                "    'body=' + bg('body')," +
                "    'text=' + text," +
                "    'bar=' + bg('.app-bar')," +
                "    'composer=' + bg('#composer')," +
                "    'send=' + bg('#send')," +
                "    'glyph=' + (shown.join('+') || '<无>')," +
                "  ].join('|');" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 在真实渲染器里驱动并测量对话流的进场动画与顶栏图标的点击回弹。
        ///
        /// 为什么非要在这里跑：这两处的正确性全落在「动画此刻是否在跑」上，
        /// 而那个状态只有真实渲染器有。具体是三件 Node 侧完全测不到的事——
        ///   · 把已在场的节点重新 append，运行中的动画会被取消并从头重播。
        ///     DOM 规范说移出文档即取消动画，而 append 一个已是子节点的元素
        ///     就是「先摘再插」。表现是气泡可见地闪两下，代码里没有任何痕迹。
        ///   · 动画被取消时触发 animationcancel 而不是 animationend，
        ///     只听后者的话类会永久留在节点上。
        ///   · 减少动效下动画根本不起播，animationend 永不触发，同样残留。
        /// getAnimations() 报的是当前实际在跑的动画对象，正是上述三件事的判据。
        ///
        /// 用注入的节点，不连真实网关：要测的是挂载与动画的相互作用，
        /// 与消息从哪来无关。
        /// </summary>
        internal string DriveMotion(string action)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => DriveMotion(action)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var encoded = System.Web.HttpUtility.JavaScriptStringEncode(action ?? string.Empty);
            var script =
                "(() => {" +
                $"  const action = '{encoded}';" +
                "  const transcript = document.getElementById('transcript');" +
                "  if (!transcript) { return '对话流不存在'; }" +
                // 挂载一律走面板自己的路径：按宿主推送的格式投一条 agent 消息给
                // 页面，由 bridge → chat.js 的 handleAgent → showPending → 真实的
                // mountToTranscript 处理。
                //
                // 为什么不在这里复刻挂载逻辑：复刻件会与真实实现漂移，那时测的
                // 是复刻件而不是面板。此前就是复刻的，修好 chat.js 之后断言照旧
                // 失败——红的是复刻件，而它已经不代表面板的行为了。
                //
                // retry 这一 stage 只做一件事：showPending(text)。首次调用新建
                // 指示器气泡并首挂，再次调用把同一个气泡重新 append 到末尾，
                // 正是要测的那条「重挂已在场节点」的真实路径。
                "  const push = (text) => {" +
                "    if (!window.chrome || !window.chrome.webview) { return false; }" +
                "    window.chrome.webview.dispatchEvent(new MessageEvent('message', {" +
                "      data: { kind: 'agent', stage: 'retry', text }," +
                "    }));" +
                "    return true;" +
                "  };" +
                "  const probe = () => document.querySelector('#transcript .msg-pending');" +
                // 工具卡片这条路径每次都新建一张卡（addToolCard），因此拿到的
                // 一定是一次全新的首挂——用来测「动画被取消时类摘不摘」。
                //
                // 不能拿指示器气泡测那件事：清 DOM 清不掉 chat.js 里的
                // pendingBubble 引用，之后的推送会走「气泡已存在」的重挂分支，
                // 那时压根没有动画可取消，断言变成假绿。
                "  const pushTool = (id) => {" +
                "    if (!window.chrome || !window.chrome.webview) { return false; }" +
                "    window.chrome.webview.dispatchEvent(new MessageEvent('message', {" +
                "      data: { kind: 'agent', stage: 'tool-start'," +
                "        payload: { id, name: 'read_range' } }," +
                "    }));" +
                "    return true;" +
                "  };" +
                "  const lastCard = () => {" +
                "    const all = document.querySelectorAll('#transcript .tool-card');" +
                "    return all.length ? all[all.length - 1] : null;" +
                "  };" +
                // 动画状态：跑着几个、叫什么、播到第几毫秒。
                "  const anim = (node) => {" +
                "    const list = node.getAnimations ? node.getAnimations() : [];" +
                "    return list.map((a) => (a.animationName || '?') + '@' +" +
                "      Math.round(Number(a.currentTime) || 0)).join('+') || '无';" +
                "  };" +
                "  if (action === 'reset') {" +
                "    transcript.replaceChildren();" +
                "    return '已清理';" +
                "  }" +
                // 首挂：推一条 retry，面板新建指示器气泡并首次挂载。
                "  if (action === 'mount') {" +
                "    if (!push('动效探针')) { return '不在宿主内，推不了消息'; }" +
                "    const node = probe();" +
                "    if (!node) { return '推送后没有出现指示器气泡'; }" +
                "    return '类=' + node.className + ' | 动画=' + anim(node) +" +
                "      ' | 序号=' + (node.dataset.seq || '无');" +
                "  }" +
                // 关键一测：再推一条，面板把同一个气泡重新 append 到末尾
                // （showPending 的重复挂载路径）。若动画被重启，currentTime 会
                // 退回接近 0，用户看到的就是这个气泡闪两下。
                "  if (action === 'remount') {" +
                "    const node = probe();" +
                "    if (!node) { return '还没有指示器气泡'; }" +
                "    const before = anim(node);" +
                "    const seqBefore = node.dataset.seq || '无';" +
                "    push('动效探针·重挂');" +
                "    const after = probe();" +
                "    return '重挂前=' + before + ' | 重挂后=' + anim(after || node) +" +
                "      ' | 同一节点=' + (after === node) +" +
                "      ' | 序号=' + seqBefore + '→' + ((after || node).dataset.seq || '无') +" +
                "      ' | 类=' + (after || node).className;" +
                "  }" +
                // 造一个全新的指示器气泡：先推 stopped 把 chat.js 的 pendingBubble
                // 引用真正清掉（clearPending），再推 retry 让它新建并首挂。
                //
                // 只清 DOM 不行——引用还在，下一条推送会走「气泡已存在」的重挂
                // 分支，于是压根没有首挂、也没有动画，据此做的断言全是假绿。
                // 首挂与重挂放进同一个 JS 任务，中间不出帧。
                //
                // 这是与时序无关的那条判据：同一任务内 animationend 绝无可能已经
                // 触发，因此进场类此刻是否还在，完全取决于代码有没有在重挂时主动
                // 摘掉它——修好的版本摘了（append 之后没有动画可起播），
                // 没修的版本没摘（append 把动画从头重播，读到 @0）。
                //
                // 需要它是因为「量进度有没有退回去」必须让渲染器出帧，那就得分两次
                // 调用；而经 COM 驱动真实 Excel 时往返远超 0.18s，重挂时动画早已
                // 自然放完，两个版本都读到「无类、无动画」——断言变成假绿。
                "  if (action === 'remount-same-task') {" +
                "    if (!window.chrome || !window.chrome.webview) { return '不在宿主内，推不了消息'; }" +
                "    window.chrome.webview.dispatchEvent(new MessageEvent('message', {" +
                "      data: { kind: 'agent', stage: 'stopped', text: '动效检查·清场' }," +
                "    }));" +
                "    transcript.replaceChildren();" +
                "    push('动效探针');" +
                "    const node = probe();" +
                "    if (!node) { return '推送后没有出现指示器气泡'; }" +
                // 字段名一律 `名=值`：调用方按这个前缀取值，写成 `名[值]` 会取不到。
                "    const first = '(' + node.className + ')(' + anim(node) + ')';" +
                "    push('动效探针·重挂');" +
                "    const after = probe();" +
                "    return '首挂=' + first + ' | 重挂后类=' + (after || node).className +" +
                "      ' | 重挂后动画=' + anim(after || node) +" +
                "      ' | 同一节点=' + (after === node);" +
                "  }" +
                // 造一个可点的空按钮，专门用来验「可点的按钮不抖」这条对照。
                //
                // 为什么不用现成的按钮：面板里可点的按钮点下去都会真的干活——
                // 「测试」会对整份目录逐个发请求（第一次就这么把检查挂住了），
                // 「刷新」会拉模型列表，「新会话」会清掉会话。对照要的只是
                // 「一个不禁用的按钮」，造一个最干净。
                "  if (action === 'add-control-button') {" +
                "    let b = document.getElementById('motion-control-button');" +
                "    if (!b) {" +
                "      b = document.createElement('button');" +
                "      b.type = 'button';" +
                "      b.id = 'motion-control-button';" +
                "      b.textContent = '对照';" +
                // 放在顶栏右侧、浮层之上，确保点得到且不被别的东西盖住。
                "      b.style.cssText = 'position:fixed;top:4px;left:4px;z-index:9999;" +
                "width:60px;height:24px';" +
                "      document.body.appendChild(b);" +
                "    }" +
                "    const r = b.getBoundingClientRect();" +
                "    return '视口坐标=' + Math.round(r.left + r.width / 2) + ',' +" +
                "      Math.round(r.top + r.height / 2) +" +
                "      ' | 缩放=' + (window.devicePixelRatio || 1) +" +
                "      ' | 禁用=' + Boolean(b.disabled);" +
                "  }" +
                // 同上，但是个禁用按钮：用来验连点的重放规则。
                //
                // 为什么不用产品里的禁用按钮做这一条：选择器的模型列表是异步拉来的，
                // 列表一到浮层内容就位移，同一坐标下的元素跟着换人。第一次跑就栽在
                // 这上面——第二下点到了隔壁的「试一下」，还真的起了一次探测。
                // 机制本身已由产品按钮（#picker-probe-all）验过，连点验的是重放规则，
                // 与点的是哪个按钮无关，用一个位置固定的更稳。
                "  if (action === 'add-disabled-button') {" +
                "    let b = document.getElementById('motion-disabled-button');" +
                "    if (!b) {" +
                "      b = document.createElement('button');" +
                "      b.type = 'button';" +
                "      b.id = 'motion-disabled-button';" +
                "      b.textContent = '禁用';" +
                "      b.disabled = true;" +
                "      b.style.cssText = 'position:fixed;top:4px;left:80px;z-index:9999;" +
                "width:60px;height:24px';" +
                "      document.body.appendChild(b);" +
                "    }" +
                "    const r = b.getBoundingClientRect();" +
                "    const cx = Math.round(r.left + r.width / 2);" +
                "    const cy = Math.round(r.top + r.height / 2);" +
                "    const hit = document.elementFromPoint(cx, cy);" +
                "    return '视口坐标=' + cx + ',' + cy +" +
                "      ' | 缩放=' + (window.devicePixelRatio || 1) +" +
                "      ' | 禁用=' + Boolean(b.disabled) +" +
                "      ' | 命中它=' + Boolean(hit && (hit === b || b.contains(hit)));" +
                "  }" +
                "  if (action === 'remove-control-button') {" +
                "    document.getElementById('motion-control-button')?.remove();" +
                "    document.getElementById('motion-disabled-button')?.remove();" +
                "    return '已移除';" +
                "  }" +
                // 报出一个禁用按钮的屏幕坐标与状态，供外部用真实鼠标去点。
                //
                // 为什么必须是真实鼠标：这套「点禁用按钮抖一下」的地基是
                // 「禁用按钮不派发点击事件，但指针命中测试照常命中它」。
                // dispatchEvent 造的事件不走命中测试，怎么造都能通——
                // 那样测的是我自己的假设，不是浏览器的行为。
                "  if (action.indexOf('disabled-at:') === 0) {" +
                "    const sel = action.slice('disabled-at:'.length);" +
                "    const node = document.querySelector(sel);" +
                "    if (!node) { return '未找到 ' + sel; }" +
                "    const r = node.getBoundingClientRect();" +
                "    if (r.width === 0 || r.height === 0) { return '元素不可见 ' + sel; }" +
                "    const cx = Math.round(r.left + r.width / 2);" +
                "    const cy = Math.round(r.top + r.height / 2);" +
                // elementFromPoint 是这套方案的另一半：它必须能拿到禁用按钮
                // 本身（或它的后代），否则文档级监听无从判断点在了哪。
                "    const hit = document.elementFromPoint(cx, cy);" +
                "    const hitsIt = Boolean(hit && (hit === node || node.contains(hit)));" +
                "    return '视口坐标=' + cx + ',' + cy +" +
                "      ' | 缩放=' + (window.devicePixelRatio || 1) +" +
                "      ' | 禁用=' + Boolean(node.disabled) +" +
                "      ' | 命中它=' + hitsIt +" +
                "      ' | 命中的是=' + (hit ? hit.tagName + '.' + (hit.className || '') : '无') +" +
                "      ' | 类=' + node.className;" +
                "  }" +
                // 读某个元素此刻的抖动状态。
                "  if (action.indexOf('refusal:') === 0) {" +
                "    const sel = action.slice('refusal:'.length);" +
                "    const node = document.querySelector(sel);" +
                "    if (!node) { return '未找到 ' + sel; }" +
                "    return '类=' + node.className + ' | 动画=' + anim(node) +" +
                "      ' | 抖动中=' + node.classList.contains('is-refusing');" +
                "  }" +
                // 记录抖动是否发生过。真实点击之后动画可能已经放完（0.19s），
                // 那时再去读类与动画都是空的，会把「放过了」误判成「没放」。
                // 所以先装一个一次性的记录器，把动画的开始与结束都记下来。
                "  if (action === 'watch-refusal') {" +
                "    window.__refusals = [];" +
                "    if (!window.__refusalWatching) {" +
                "      window.__refusalWatching = true;" +
                // 也记下到达文档的 pointerdown。少了这一条，「没抖」分不清是
                // 事件没来（点偏了、被别的东西吃掉了）还是来了却没放动画，
                // 而这两者的修法完全不同。
                "      document.addEventListener('pointerdown', (e) => {" +
                "        const hit = document.elementFromPoint(e.clientX, e.clientY);" +
                "        window.__refusals.push('按下@' + e.clientX + ',' + e.clientY +" +
                "          ':' + (hit ? (hit.id || hit.className || hit.tagName) : '无') +" +
                "          ':次数' + e.detail);" +
                "      }, true);" +
                "      document.addEventListener('animationstart', (e) => {" +
                "        if (e.animationName !== 'refuse-shake') { return; }" +
                "        window.__refusals.push('开始:' + (e.target.id || e.target.className));" +
                "      }, true);" +
                "      document.addEventListener('animationend', (e) => {" +
                "        if (e.animationName !== 'refuse-shake') { return; }" +
                "        window.__refusals.push('结束:' + (e.target.id || e.target.className) +" +
                "          ':残留=' + e.target.classList.contains('is-refusing'));" +
                "      }, true);" +
                "    }" +
                "    return '已开始记录';" +
                "  }" +
                "  if (action === 'refusals') {" +
                "    const list = window.__refusals || [];" +
                "    return '条数=' + list.length + ' | 记录=' + (list.join(' / ') || '无');" +
                "  }" +
                "  if (action === 'fresh') {" +
                "    if (!window.chrome || !window.chrome.webview) { return '不在宿主内，推不了消息'; }" +
                "    window.chrome.webview.dispatchEvent(new MessageEvent('message', {" +
                "      data: { kind: 'agent', stage: 'stopped', text: '动效检查·清场' }," +
                "    }));" +
                "    transcript.replaceChildren();" +
                "    push('动效探针');" +
                "    const node = probe();" +
                "    if (!node) { return '推送后没有出现指示器气泡'; }" +
                "    return '类=' + node.className + ' | 动画=' + anim(node);" +
                "  }" +
                // 新建一张工具卡片。每次都是全新首挂，动画一定在跑。
                "  if (action === 'card') {" +
                // 上一张卡的引用要清掉：card-state 优先读它，不清的话新建一张卡
                // 之后读到的仍是旧卡，断言就测错了对象。
                "    window.__motionCard = null;" +
                "    if (!pushTool('motion-' + Date.now())) { return '不在宿主内，推不了消息'; }" +
                "    const card = lastCard();" +
                "    if (!card) { return '推送后没有出现工具卡片'; }" +
                "    return '类=' + card.className + ' | 动画=' + anim(card);" +
                "  }" +
                // 把仍在动的卡片搬进一个未渲染的容器：sealOpsBatch 把卡片搬进
                // details 的 body 就是这条路径，它触发 animationcancel 而不是
                // animationend——只听后者的话类会永久残留。
                "  if (action === 'move-card-away') {" +
                "    const card = lastCard();" +
                "    if (!card) { return '还没有工具卡片'; }" +
                "    const before = anim(card);" +
                "    const box = document.createElement('div');" +
                "    box.className = 'ops-body';" +
                "    box.append(card);" +
                "    transcript.append(box);" +
                "    window.__motionCard = card;" +
                "    return '搬前=' + before + ' | 搬后=' + anim(card) +" +
                "      ' | 类=' + card.className;" +
                "  }" +
                "  if (action === 'card-state') {" +
                "    const card = window.__motionCard || lastCard();" +
                "    if (!card) { return '还没有工具卡片'; }" +
                "    return '类=' + card.className + ' | 动画=' + anim(card) +" +
                "      ' | 残留=' + card.classList.contains('is-entering');" +
                "  }" +
                "  if (action === 'state') {" +
                "    const node = probe();" +
                "    if (!node) { return '还没有指示器气泡'; }" +
                "    return '类=' + node.className + ' | 动画=' + anim(node) +" +
                "      ' | 残留=' + node.classList.contains('is-entering');" +
                "  }" +
                // 顶栏图标：点一下，读它的 svg 此刻在跑什么动画。
                "  if (action.indexOf('tap:') === 0) {" +
                "    const id = action.slice('tap:'.length);" +
                "    const button = id === 'theme'" +
                "      ? document.getElementById('theme-toggle')" +
                "      : document.querySelector('.app-nav .nav-btn[data-route=\"' + id + '\"]');" +
                "    if (!button) { return '未找到按钮 ' + id; }" +
                "    button.click();" +
                "    const svg = [...button.querySelectorAll('svg')]" +
                "      .find((s) => getComputedStyle(s).display !== 'none') ||" +
                "      button.querySelector('svg');" +
                "    return '类=' + button.className +" +
                "      ' | 动画=' + (svg ? anim(svg) : '无svg') +" +
                "      ' | 绑定=' + (button.matches('.app-nav .nav-btn'));" +
                "  }" +
                // 连点：第二下必须重新起播。相同类名再 add 不会重启动画，
                // 靠的是回调里「先摘、读一次布局、再挂」。
                "  if (action.indexOf('tap-twice:') === 0) {" +
                "    const id = action.slice('tap-twice:'.length);" +
                "    const button = id === 'theme'" +
                "      ? document.getElementById('theme-toggle')" +
                "      : document.querySelector('.app-nav .nav-btn[data-route=\"' + id + '\"]');" +
                "    if (!button) { return '未找到按钮 ' + id; }" +
                "    const svg = () => [...button.querySelectorAll('svg')]" +
                "      .find((s) => getComputedStyle(s).display !== 'none') ||" +
                "      button.querySelector('svg');" +
                "    button.click();" +
                "    const first = anim(svg());" +
                "    button.click();" +
                "    const second = anim(svg());" +
                "    return '第一下=' + first + ' | 第二下=' + second;" +
                "  }" +
                "  return '未知动作 ' + action;" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 读取宿主控件自身的底色，格式 R,G,B。
        /// 这一圈在 WebView2 之外，页面 CSS 管不到，深色下漏涂就是一块白边。
        /// </summary>
        internal string ReadPaneBackColor()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(ReadPaneBackColor));
            }

            return $"{BackColor.R},{BackColor.G},{BackColor.B}";
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

        /// <summary>
        /// 读取轮次操作组的状态：组数、批中待收的卡片数，以及每组的摘要、
        /// 卡片数与展开状态。
        ///
        /// 一轮的操作在下一轮开始时收成一组，因此这个钩子回答的是
        /// 「上几轮的操作是否真的收起来了、摘要里的统计对不对」——
        /// 只数 .tool-card 是看不出来的，卡片进了组仍在 DOM 里。
        /// </summary>
        internal string ReadOperationGroups()
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ReadOperationGroups()));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const groups = [...document.querySelectorAll('.ops-group')];" +
                "  const flat = [...document.querySelectorAll('#transcript > .tool-card')];" +
                "  const parts = ['组数=' + groups.length, '组外卡片=' + flat.length];" +
                "  groups.forEach((g, i) => {" +
                "    const label = (g.querySelector('.ops-label')?.textContent ?? '').trim();" +
                "    const cards = g.querySelectorAll('.tool-card').length;" +
                "    parts.push(" +
                "      '组' + (i + 1) + '=' + label +" +
                "      '/卡片' + cards +" +
                "      '/' + (g.open ? '展开' : '收起') +" +
                "      '/' + (g.classList.contains('is-error') ? '有失败' : '无失败') +" +
                "      '/还原入口' + (g.querySelector('.ops-restore') ? '有' : '无'));" +
                "  });" +
                "  return parts.join(' | ');" +
                "})()";

            return RunScriptSync(script, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// 点第 index 个轮次操作组上的「还原」按钮，index 从 0 起。
        /// 返回点击结果，供验证还原后卡片是否回到对话流原位。
        /// </summary>
        internal string ClickRestoreOperationGroup(int index)
        {
            if (InvokeRequired)
            {
                return (string)Invoke(new Func<string>(() => ClickRestoreOperationGroup(index)));
            }

            if (!_webViewReady || _webView?.CoreWebView2 == null)
            {
                return "WebView2 尚未就绪";
            }

            var script =
                "(() => {" +
                "  const groups = document.querySelectorAll('.ops-group');" +
                $"  const group = groups[{index}];" +
                "  if (!group) { return '没有第 " + (index + 1) + " 个操作组，共 ' + groups.length + ' 个'; }" +
                "  const button = group.querySelector('.ops-restore');" +
                "  if (!button) { return '该组没有还原入口'; }" +
                "  const before = group.querySelectorAll('.tool-card').length;" +
                "  button.click();" +
                "  return '已还原 ' + before + ' 张卡片，剩余组数=' +" +
                "    document.querySelectorAll('.ops-group').length;" +
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

        private string _theme = string.Empty;

        /// <summary>
        /// 面板报来的主题：给页面之外的部分上色并存档。
        ///
        /// 要涂的有三处，缺一处就会在深色下露出一块白：
        /// 承载控件本身、初始化期间的占位文字、以及 WebView2 在页面绘制完成前
        /// 显示的默认底色（导航过程中也会短暂露出来）。
        /// </summary>
        internal bool ApplyTheme(string theme)
        {
            if (theme != "light" && theme != "dark")
            {
                return false;
            }

            if (InvokeRequired)
            {
                return (bool)Invoke(new Func<bool>(() => ApplyTheme(theme)));
            }

            try
            {
                _theme = theme;
                var back = PaneBackColor(theme);
                BackColor = back;

                if (_fallback != null)
                {
                    _fallback.ForeColor = PaneForeColor(theme);
                }

                if (_webViewReady && _webView?.CoreWebView2 != null)
                {
                    _webView.DefaultBackgroundColor = back;
                }

                PersistTheme(theme);
                return true;
            }
            catch (Exception ex)
            {
                // 上不了色只是深色下开面板会闪一下白，功能不受影响。
                Log.Warn("应用面板主题失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 与 app.css 的 --bg 对齐：浅色 #ffffff、深色 #1b1d21。
        /// 两边对不上会在页面边缘留下一条色差。
        /// </summary>
        private static Color PaneBackColor(string theme)
            => theme == "dark" ? Color.FromArgb(0x1B, 0x1D, 0x21) : Color.White;

        /// <summary>占位文字的颜色。深色下用 --text-muted 的近似值。</summary>
        private static Color PaneForeColor(string theme)
            => theme == "dark" ? Color.FromArgb(0xA0, 0xA6, 0xAD) : Color.FromArgb(70, 70, 70);

        private static string LoadStoredTheme()
        {
            try
            {
                return Storage.Settings.Load().Theme ?? string.Empty;
            }
            catch (Exception ex)
            {
                // 读不到就按浅色起步，面板加载完会立刻报来真实主题。
                Log.Warn("读取记录的面板主题失败：" + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>只在真的变了时落盘，避免每次打开面板都重写设置文件。</summary>
        private static void PersistTheme(string theme)
        {
            try
            {
                var settings = Storage.Settings.Load();
                if (settings.Theme == theme)
                {
                    return;
                }

                settings.Theme = theme;
                settings.Save();
            }
            catch (Exception ex)
            {
                Log.Warn("记录面板主题失败：" + ex.Message);
            }
        }

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
