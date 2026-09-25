using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ChatSheet.AddIn;
using ChatSheet.AddIn.Interop;
using ChatWord.AddIn.Bridge;

namespace ChatWord.AddIn
{
    [ComVisible(true)]
    [Guid(ComIds.AddInClsid)]
    [ProgId(ComIds.AddInProgId)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed partial class WordComAddIn : IDTExtensibility2, ICustomTaskPaneConsumer, IRibbonExtensibility, IReflect
    {
        private object _application;
        private ICTPFactory _paneFactory;
        private ChatSheet.AddIn.TaskPaneController _pane;
        private object _ribbonUi;
        private Timer _paneVisibilityTimer;
        private int _paneVisibilityAttempts;
        private Timer _ribbonRefreshTimer;
        private int _ribbonRefreshAttempts;
        private bool _autoPaneRequested;

        public void OnConnection(object application, ext_ConnectMode connectMode, object addInInst, ref Array custom)
        {
            try { _application = application; ChatSheet.AddIn.Log.Info("Office-helper Word OnConnection：" + Hosts.WordHostProbe.Describe(application, ComIds.AddInProgId)); }
            catch (Exception ex) { ChatSheet.AddIn.Log.Error("Office-helper Word OnConnection 失败", ex); }
        }

        public void OnDisconnection(ext_DisconnectMode removeMode, ref Array custom)
        {
            try
            {
                StopPaneVisibilityRetry();
                StopRibbonRefresh();
                _pane?.Dispose();
            }
            catch { }
            finally { _pane = null; _paneFactory = null; _application = null; _ribbonUi = null; }
        }

        public void OnAddInsUpdate(ref Array custom) { }
        public void OnStartupComplete(ref Array custom)
        {
            StartRibbonRefresh();
        }
        public void OnBeginShutdown(ref Array custom) { }

        public void CTPFactoryAvailable(ICTPFactory cTPFactoryInst)
        {
            _paneFactory = cTPFactoryInst;
            ChatSheet.AddIn.Log.Info("Office-helper Word CTPFactoryAvailable");
            Invalidate();
        }

        public string GetCustomUI(string ribbonID)
        {
            try
            {
                ChatSheet.AddIn.Log.Info("Office-helper Word GetCustomUI：" + ribbonID);
                var name = Assembly.GetExecutingAssembly().GetName().Name + ".Resources.Ribbon.xml";
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                using (var reader = stream == null ? null : new StreamReader(stream))
                { return reader?.ReadToEnd() ?? string.Empty; }
            }
            catch (Exception ex) { ChatSheet.AddIn.Log.Error("读取 Word Ribbon 失败", ex); return string.Empty; }
        }

        public void OnRibbonLoad(object ribbonUi)
        {
            _ribbonUi = ribbonUi;
            ChatSheet.AddIn.Log.Info("Office-helper Word OnRibbonLoad");
            StartRibbonRefresh();
        }

        public void OnTogglePaneHome(object control)
        {
            RequestPaneVisible();
        }

        public void OnTogglePane(object control)
        {
            StopPaneVisibilityRetry();
            EnsurePane();
            if (_pane != null) { _pane.IsVisible = !_pane.IsVisible; Invalidate(); }
        }

        // toggleButton 的回调包含 pressed 参数；保留普通 button 使用的单参数入口。
        public void OnTogglePaneToggle(object control, bool pressed)
        {
            StopPaneVisibilityRetry();
            EnsurePane();
            if (_pane != null) { _pane.IsVisible = pressed; Invalidate(); }
        }

        public bool OnGetPanePressed(object control) { return _pane != null && _pane.IsVisible; }
        public void OnSettings(object control) { RequestPaneVisible(); _pane?.Navigate("settings"); }
        public void OnDiagnostics(object control) { RequestPaneVisible(); _pane?.Navigate("diagnostics"); }
        public void OnUndo(object control) { RequestPaneVisible(); }
        public bool OnGetUndoEnabled(object control) { return false; }
        public object GetPaneImage(object control) { return null; }

        private void EnsurePane()
        {
            if (_pane != null || _paneFactory == null) { return; }
            _pane = ChatSheet.AddIn.TaskPaneController.Create(
                _paneFactory,
                _application,
                ComIds.TaskPaneProgId,
                ComIds.PaneTitle,
                (core, accessor) => new WordBridge(core, accessor));
        }

        private void RequestPaneVisible()
        {
            EnsurePane();
            if (_pane == null) { return; }

            _pane.TrySetVisible(true);

            if (_paneVisibilityTimer == null)
            {
                _paneVisibilityTimer = new Timer { Interval = 1000 };
                _paneVisibilityTimer.Tick += OnPaneVisibilityRetry;
            }

            _paneVisibilityAttempts = 0;
            _paneVisibilityTimer.Start();
        }

        private void OnPaneVisibilityRetry(object sender, EventArgs e)
        {
            if (_pane == null)
            {
                StopPaneVisibilityRetry();
                return;
            }

            if (_pane.TrySetVisible(true))
            {
                _paneVisibilityAttempts = 0;
                Invalidate();
                StopPaneVisibilityRetry();
                return;
            }

            _paneVisibilityAttempts++;
            if (_paneVisibilityAttempts == 20)
            {
                ChatSheet.AddIn.Log.Warn("WPS 文档窗格在延迟重试后仍未显示");
            }
        }

        private void StopPaneVisibilityRetry()
        {
            if (_paneVisibilityTimer == null) { return; }
            _paneVisibilityTimer.Stop();
            _paneVisibilityTimer.Dispose();
            _paneVisibilityTimer = null;
            _paneVisibilityAttempts = 0;
        }

        private void StartRibbonRefresh()
        {
            if (_ribbonRefreshTimer == null)
            {
                _ribbonRefreshTimer = new Timer { Interval = 500 };
                _ribbonRefreshTimer.Tick += OnRibbonRefresh;
            }

            _ribbonRefreshAttempts = 0;
            _ribbonRefreshTimer.Start();
        }

        private void OnRibbonRefresh(object sender, EventArgs e)
        {
            _ribbonRefreshAttempts++;

            // WPS 会在 CTPFactoryAvailable 仍处于文档窗口初始化阶段时回调。
            // 直接在那个回调里 CreateCTP 会得到一个 Visible=false 的隐藏窗格；
            // 等消息循环完成一次功能区刷新后再创建，双击文档和手动打开走同一实例。
            if (!_autoPaneRequested && _pane == null && _paneFactory != null)
            {
                _autoPaneRequested = true;
                ChatSheet.AddIn.Log.Info("Office-helper Word 自动显示面板：宿主启动刷新完成");
                RequestPaneVisible();
            }

            Invalidate();
            if (_ribbonRefreshAttempts >= 12)
            {
                StopRibbonRefresh();
            }
        }

        private void StopRibbonRefresh()
        {
            if (_ribbonRefreshTimer == null) { return; }
            _ribbonRefreshTimer.Stop();
            _ribbonRefreshTimer.Dispose();
            _ribbonRefreshTimer = null;
            _ribbonRefreshAttempts = 0;
            _autoPaneRequested = false;
        }

        private void Invalidate()
        {
            try { if (_ribbonUi != null) { Hosts.WordCom.Call(_ribbonUi, "Invalidate"); } }
            catch { }
        }
    }
}
