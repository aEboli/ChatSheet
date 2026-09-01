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
        private bool _promptShowing;

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

                // 目标状态按面板的实际可见性取反，而不是直接采用 pressed。
                //
                // 因为 pressed 会与实际状态脱节：用户用面板自己的关闭按钮关掉面板时，
                // 宿主不通知加载项，功能区仍按「按下」记着。下一次点击于是送来
                // pressed=false，去隐藏一个已经隐藏的面板——点了没反应。
                // 读得到真实状态时就以它为准，读不到才退回 pressed。
                var target = _pane != null && _pane.TryGetVisible(out var visible) ? !visible : pressed;

                // 面板挂在别的工作簿窗口上时，它报告的可见性对当前窗口没有意义：
                // 用户眼前没有面板，此时的点击一定是「要打开」，不能按取反去隐藏。
                // 重建交给 ApplyPaneVisibility 统一处理，这里只纠正目标状态。
                if (_pane != null && !_pane.IsParentedToActiveWindow(_application))
                {
                    Log.Info("面板挂在其他工作簿窗口上，本次按「打开」处理");
                    target = true;
                }
                else if (target != pressed)
                {
                    Log.Info($"功能区按下态与面板实际状态不一致，按实际状态取反：目标={target}");
                }

                ApplyPaneVisibility(target, null, interactive: true);
            }
            catch (Exception ex)
            {
                Log.Error("OnTogglePane 失败", ex);
            }
        }

        public void OnOpenSettings(object control)
        {
            ShowPaneRoute("settings", interactive: true);
        }

        public void OnOpenDiagnostics(object control)
        {
            ShowPaneRoute("diagnostics", interactive: true);
        }

        private void ShowPaneRoute(string route, bool interactive)
        {
            try
            {
                ApplyPaneVisibility(true, route, interactive);
            }
            catch (Exception ex)
            {
                Log.Error($"打开面板路由 {route} 失败", ex);
            }
        }

        /// <summary>
        /// 显示或隐藏面板，失败时判定成因并（仅在用户手势路径上）给出提示。
        ///
        /// 隐藏失败不提示：用户要的是面板消失，而面板确实不在眼前，
        /// 为此弹一个框只是添乱，记日志就够了。
        /// </summary>
        /// <param name="visible">目标可见性。</param>
        /// <param name="route">显示后要导航到的页，null 表示不导航。</param>
        /// <param name="interactive">是否由真实用户手势触发，只有它为真才允许弹窗。</param>
        private void ApplyPaneVisibility(bool visible, string route, bool interactive)
        {
            var createAttempted = false;
            var createSucceeded = true;

            // 要显示面板时，先确认它挂在当前窗口上。挂在别处的窗格无法改绑，
            // 只能就地重建，否则用户看到的仍是空无一物。
            if (visible && _pane != null && !_pane.IsParentedToActiveWindow(_application))
            {
                Log.Info("面板挂在其他工作簿窗口上，重建后再显示");
                DisposePane();
            }

            if (_pane == null)
            {
                createAttempted = true;
                createSucceeded = EnsurePane();
            }

            var shown = false;
            if (_pane != null)
            {
                shown = _pane.TrySetVisible(visible);

                // 窗格对象还在，但宿主已经把它拆掉了——最典型的是关掉工作簿窗口，
                // 窗格随窗口一起销毁，而这里的引用仍然非空。这种状态下每次点击
                // 都只会重复失败，除非重建。重建一次并重试，代价是本次会话的
                // 对话内容重新开始，但这好过面板此后再也打不开。
                if (!shown && visible)
                {
                    Log.Warn("显示面板失败，判定窗格已失效，重建后重试");
                    DisposePane();
                    createAttempted = true;
                    createSucceeded = EnsurePane();
                    if (_pane != null)
                    {
                        shown = _pane.TrySetVisible(true);
                    }
                }
            }

            if (shown && !string.IsNullOrEmpty(route))
            {
                _pane.Navigate(route);
            }

            InvalidateRibbon();

            if (shown || !visible)
            {
                return;
            }

            var blocker = PaneOpenDiagnosis.Classify(
                Hosts.HostProbe.ReadWindowState(_application),
                factoryReady: _paneFactory != null,
                createAttempted: createAttempted,
                createSucceeded: createSucceeded,
                showSucceeded: false);

            Log.Warn("面板未能打开，判定成因：" + blocker);

            if (interactive)
            {
                ShowBlockerPrompt(blocker);
            }
        }

        /// <summary>
        /// 弹出成因提示。
        ///
        /// 只在真实用户手势路径上调用：自动化接口也走同一套显示逻辑，
        /// 而模态框会让跨进程的 COM 调用一直不返回，
        /// 验证脚本就会从「报错退出」变成「挂住不动」。
        /// </summary>
        private void ShowBlockerPrompt(PaneBlocker blocker)
        {
            var text = PaneOpenDiagnosis.Compose(blocker, Log.CurrentPath);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // 模态框会起一个嵌套消息循环，期间宿主继续派发消息，
            // 用户可以再点一次按钮把第二个框叠上来。这个闸门挡住重入。
            if (_promptShowing)
            {
                return;
            }

            _promptShowing = true;
            try
            {
                var owner = HostOwnerWindow();

                // 弹出前后各记一条，别当调试残留删掉。
                //
                // 提示框在场时，宿主主线程正卡在模态框的嵌套消息循环里，
                // 此时对该进程窗口的 UI 自动化属性读取会超时抛异常，
                // 外部工具据此会得出「压根没弹框」的错误结论。
                // 这两条日志是唯一能分清「弹了」与「没弹」的证据，
                // 缺了它们，验证脚本只能靠猜。
                Log.Info($"准备显示成因提示：所有者句柄={(owner == null ? "无" : owner.Handle.ToString())}");
                var result = System.Windows.Forms.MessageBox.Show(
                    owner,
                    text,
                    "ChatSheet 面板打不开",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
                Log.Info($"成因提示已关闭：{result}");
            }
            catch (Exception ex)
            {
                // 弹不出来也不能影响宿主，成因已经在日志里。
                Log.Warn("显示成因提示失败：" + ex.Message);
            }
            finally
            {
                _promptShowing = false;
            }
        }

        /// <summary>
        /// 取宿主主窗口作为模态框的所有者，避免对话框跑到 Excel 后面去，
        /// 那会表现为 Excel 整个卡住不响应。取不到时退回无所有者。
        /// </summary>
        private System.Windows.Forms.IWin32Window HostOwnerWindow()
        {
            try
            {
                if (_application != null &&
                    Hosts.Com.TryGet(_application, "Hwnd", out var raw) && raw != null)
                {
                    var handle = new IntPtr(Convert.ToInt64(raw));
                    if (handle != IntPtr.Zero)
                    {
                        return new HostWindow(handle);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("取宿主窗口句柄失败：" + ex.Message);
            }

            return null;
        }

        /// <summary>把宿主窗口句柄包成 WinForms 能用的所有者。</summary>
        private sealed class HostWindow : System.Windows.Forms.IWin32Window
        {
            internal HostWindow(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }

        /// <summary>创建面板。返回是否可用，让调用方能把失败纳入成因判定。</summary>
        private bool EnsurePane()
        {
            if (_pane != null)
            {
                return true;
            }

            if (_paneFactory == null)
            {
                Log.Warn("窗格工厂尚未就绪，无法创建面板");
                return false;
            }

            _pane = TaskPaneController.Create(_paneFactory, _application);
            return _pane != null;
        }

        private void DisposePane()
        {
            try
            {
                _pane?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("释放失效窗格失败：" + ex.Message);
            }
            finally
            {
                _pane = null;
            }
        }

        // ---- 供 AddInAutomation 调用的入口。功能区按钮只能由真实点击触发，
        // 这些方法让脚本与自动化验证也能控制面板。 ----

        /// <summary>
        /// 供自动化调用的显示入口。
        /// interactive 传 false：模态框会让跨进程的 COM 调用一直不返回，
        /// 验证脚本会从「报错退出」变成「挂住不动」。
        /// </summary>
        internal void ShowPaneForAutomation(string route)
        {
            ShowPaneRoute(route, interactive: false);
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

        internal string DriveMotionForAutomation(string action)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.DriveMotion(action);
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

        internal string ReadQueueForAutomation()
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ReadQueue();
        }

        internal string CancelQueuedForAutomation(int index)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.CancelQueued(index);
        }

        internal string ClickSendForAutomation()
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ClickSend();
        }

        internal string ClickFitForAutomation(string alignment)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ClickFit(alignment);
        }

        internal string ClickThemeToggleForAutomation()
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ClickThemeToggle();
        }

        internal string ReadThemeStateForAutomation()
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ReadThemeState();
        }

        internal string ReadLastNoticeForAutomation()
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ReadLastNotice();
        }

        internal string ReadLastToolCardForAutomation()
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ReadLastToolCard();
        }

        internal string ReadOperationGroupsForAutomation()
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ReadOperationGroups();
        }

        internal string ClickRestoreOperationGroupForAutomation(int index)
        {
            if (_pane == null)
            {
                return "面板尚未创建";
            }

            return _pane.ClickRestoreOperationGroup(index);
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
