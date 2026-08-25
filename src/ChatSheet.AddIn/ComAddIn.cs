using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using ChatSheet.AddIn.Hosts;
using ChatSheet.AddIn.Interop;

namespace ChatSheet.AddIn
{
    /// <summary>
    /// 加载项入口。同时被 Microsoft Excel 与 WPS 表格加载。
    /// 约束：任何 COM 入口抛出异常都会让宿主把加载项标记为禁用，
    /// 所以这里每个回调都必须自行兜住异常并记录日志。
    /// </summary>
    [ComVisible(true)]
    [Guid(ComIds.AddInClsid)]
    [ProgId(ComIds.AddInProgId)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed partial class ComAddIn : IDTExtensibility2, ICustomTaskPaneConsumer, IRibbonExtensibility
    {
        private object _application;
        private object _addInInstance;
        private ICTPFactory _paneFactory;
        private object _ribbonUi;
        private TaskPaneController _pane;

        public ComAddIn()
        {
            // 构造即打点：这是判定「宿主是否真的实例化了本类」的最早证据。
            Beacon.Mark("ctor");
        }

        public void OnConnection(object application, ext_ConnectMode connectMode, object addInInst, ref Array custom)
        {
            try
            {
                Beacon.Mark("OnConnection", connectMode.ToString());
                _application = application;
                _addInInstance = addInInst;
                Log.Info($"OnConnection 模式={connectMode} 宿主={HostProbe.DescribeSafely(application)} 位数={(Environment.Is64BitProcess ? "x64" : "x86")}");

                // 挂上自动化接口：宿主会把它暴露为 COMAddIns("ChatSheet.AddIn").Object。
                // 失败不影响加载项本身，只是脚本控制不可用。
                try
                {
                    Hosts.Com.Set(addInInst, "Object", new AddInAutomation(this));
                }
                catch (Exception ex)
                {
                    Log.Warn("挂载自动化接口失败：" + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Log.Error("OnConnection 失败", ex);
            }
        }

        public void OnDisconnection(ext_DisconnectMode removeMode, ref Array custom)
        {
            try
            {
                Log.Info($"OnDisconnection 模式={removeMode}");
                _pane?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("OnDisconnection 失败", ex);
            }
            finally
            {
                _pane = null;
                _paneFactory = null;
                _ribbonUi = null;
                _addInInstance = null;
                _application = null;
            }
        }

        public void OnAddInsUpdate(ref Array custom)
        {
        }

        public void OnStartupComplete(ref Array custom)
        {
            try
            {
                Log.Info("OnStartupComplete");
            }
            catch (Exception ex)
            {
                Log.Error("OnStartupComplete 失败", ex);
            }
        }

        public void OnBeginShutdown(ref Array custom)
        {
        }

        /// <summary>
        /// 宿主在窗格工厂就绪时回调。此时不能立刻建窗格：
        /// 该回调早于 OnStartupComplete，部分宿主此刻建窗格会失败。
        /// 这里只保存工厂，等用户点功能区按钮时再创建。
        /// </summary>
        public void CTPFactoryAvailable(ICTPFactory cTPFactoryInst)
        {
            try
            {
                Beacon.Mark("CTPFactoryAvailable");
                _paneFactory = cTPFactoryInst;
                Log.Info("CTPFactoryAvailable：窗格工厂已就绪");
            }
            catch (Exception ex)
            {
                Log.Error("CTPFactoryAvailable 失败", ex);
            }
        }

        public string GetCustomUI(string ribbonID)
        {
            try
            {
                Beacon.Mark("GetCustomUI", ribbonID);
                Log.Info($"GetCustomUI ribbonID={ribbonID}");
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetName().Name + ".Resources.Ribbon.xml";
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Log.Error($"功能区资源缺失：{resourceName}", null);
                        return string.Empty;
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("GetCustomUI 失败", ex);
                return string.Empty;
            }
        }

        // ---- 功能区回调。名字必须与 Resources\Ribbon.xml 完全一致。 ----

        public void OnRibbonLoad(object ribbonUi)
        {
            try
            {
                _ribbonUi = ribbonUi;
                Log.Info("功能区已加载");
            }
            catch (Exception ex)
            {
                Log.Error("OnRibbonLoad 失败", ex);
            }
        }

        public bool OnGetPanePressed(object control)
        {
            try
            {
                return _pane != null && _pane.IsVisible;
            }
            catch (Exception ex)
            {
                Log.Error("OnGetPanePressed 失败", ex);
                return false;
            }
        }

        public void OnTogglePane(object control, bool pressed)
        {
            try
            {
                // 记录来源：侧边栏的显示/隐藏若出现非预期的自动触发，
                // 这条日志是唯一的定位线索。
                Log.Info($"OnTogglePane pressed={pressed}");
                EnsurePane();
                if (_pane != null)
                {
                    _pane.IsVisible = pressed;
                }
            }
            catch (Exception ex)
            {
                Log.Error("OnTogglePane 失败", ex);
            }
        }

        public void OnOpenSettings(object control)
        {
            ShowPaneRoute("settings");
        }

        public void OnOpenDiagnostics(object control)
        {
            ShowPaneRoute("diagnostics");
        }

        private void ShowPaneRoute(string route)
        {
            try
            {
                EnsurePane();
                if (_pane == null)
                {
                    return;
                }

                _pane.IsVisible = true;
                _pane.Navigate(route);
                InvalidateRibbon();
            }
            catch (Exception ex)
            {
                Log.Error($"打开面板路由 {route} 失败", ex);
            }
        }

        private void EnsurePane()
        {
            if (_pane != null)
            {
                return;
            }

            if (_paneFactory == null)
            {
                Log.Warn("窗格工厂尚未就绪，无法创建面板");
                return;
            }

            _pane = TaskPaneController.Create(_paneFactory, _application);
        }

        // ---- 供 AddInAutomation 调用的入口。功能区按钮只能由真实点击触发，
        // 这些方法让脚本与自动化验证也能控制面板。 ----

        internal void ShowPaneForAutomation(string route)
        {
            ShowPaneRoute(route);
        }

        internal void HidePaneForAutomation()
        {
            if (_pane != null)
            {
                _pane.IsVisible = false;
                InvalidateRibbon();
            }
        }

        internal bool IsPaneVisibleForAutomation()
        {
            return _pane != null && _pane.IsVisible;
        }

        internal int GetPaneWidthForAutomation()
        {
            return _pane?.Width ?? -1;
        }

        internal int SetPaneWidthForAutomation(int width)
        {
            if (_pane == null)
            {
                return -1;
            }

            _pane.Width = width;
            return _pane.Width;
        }

        internal string SendChatForAutomation(string text)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.SendChat(text);
        }

        internal string ClickApprovalForAutomation(bool approve)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ClickApproval(approve);
        }

        internal string DrivePickerForAutomation(string action)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.DrivePicker(action);
        }

        internal string ReadElementTextForAutomation(string elementId)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ReadElementText(elementId);
        }

        internal string ReadComposerTextForAutomation()
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ReadComposerText();
        }

        internal string ClickUndoForAutomation(int index)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ClickUndo(index);
        }

        internal string AttachImageForAutomation(string dataUrl, string name)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.AttachImage(dataUrl, name);
        }

        private void InvalidateRibbon()
        {
            try
            {
                if (_ribbonUi != null)
                {
                    Hosts.Com.Call(_ribbonUi, "Invalidate");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("功能区刷新失败：" + ex.Message);
            }
        }
    }
}
