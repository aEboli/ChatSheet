using System;
using ChatSheet.AddIn.Hosts;
using ChatSheet.AddIn.Interop;

namespace ChatSheet.AddIn
{
    /// <summary>
    /// 侧边栏包装。宿主返回的窗格对象类型库不同（Excel 与 WPS 各一套），
    /// 因此一律用后期绑定访问其成员。
    /// </summary>
    internal sealed class TaskPaneController : IDisposable
    {
        private object _pane;
        private TaskPaneControl _control;

        private TaskPaneController(object pane, TaskPaneControl control)
        {
            _pane = pane;
            _control = control;
        }

        internal static TaskPaneController Create(ICTPFactory factory, object application)
        {
            object pane = null;
            try
            {
                // CreateCTP 按 ProgID 经 COM 实例化控件，所以控件必须已注册为 ActiveX 控件。
                pane = factory.CreateCTP(ComIds.TaskPaneProgId, ComIds.PaneTitle, Type.Missing);
                if (pane == null)
                {
                    Log.Error("CreateCTP 返回 null", null);
                    return null;
                }

                // 停靠到右侧：2 = msoCTPDockPositionRight。
                TrySet(pane, "DockPosition", 2);

                var control = ResolveControl(pane);
                if (control == null)
                {
                    Log.Error("窗格已创建但取不到托管控件实例", null);
                }
                else
                {
                    control.Attach(application);
                }

                Log.Info("侧边栏创建成功");
                var controller = new TaskPaneController(pane, control);
                control?.AttachWidthHandlers(controller.AdjustWidthForCss, controller.PersistCurrentWidth);
                return controller;
            }
            catch (Exception ex)
            {
                Log.Error("创建侧边栏失败", ex);
                Com.Release(pane);
                return null;
            }
        }

        /// <summary>
        /// 取回托管控件实例。首选窗格的 ContentControl；
        /// 部分宿主返回的包装无法直接转换，于是回退到控件自身登记的最近实例。
        /// </summary>
        private static TaskPaneControl ResolveControl(object pane)
        {
            try
            {
                if (Com.TryGet(pane, "ContentControl", out var content) && content is TaskPaneControl typed)
                {
                    return typed;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("ContentControl 取值失败：" + ex.Message);
            }

            return TaskPaneControl.LastCreated;
        }

        internal bool IsVisible
        {
            get
            {
                try
                {
                    return _pane != null && Com.TryGet(_pane, "Visible", out var value) && Convert.ToBoolean(value);
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                try
                {
                    if (_pane == null)
                    {
                        return;
                    }

                    Com.Set(_pane, "Visible", value);

                    if (value)
                    {
                        RestoreWidth();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("设置侧边栏可见性失败", ex);
                }
            }
        }

        /// <summary>宿主报告的窗格宽度。读写都可能被宿主拒绝或调整。</summary>
        internal int Width
        {
            get
            {
                try
                {
                    return _pane != null && Com.TryGet(_pane, "Width", out var raw) && raw != null
                        ? Convert.ToInt32(raw)
                        : -1;
                }
                catch
                {
                    return -1;
                }
            }
            set
            {
                try
                {
                    if (_pane != null)
                    {
                        Com.Set(_pane, "Width", value);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"设置侧边栏宽度为 {value} 失败：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 按面板报告的 CSS 宽度校准宿主宽度，并记住结果。
        ///
        /// 用增量而非比例换算：宿主宽度里含边框与滚动条等固定开销，
        /// 「宿主宽度 ÷ CSS 宽度」会把这段常量摊进系数，测量稍有偏差就整体放大，
        /// 实测曾因此把目标 400 CSS 拉到 452。改成
        /// 「目标宿主宽度 = 当前宿主宽度 + CSS 差值 × 设备像素比」后常量自然抵消，
        /// 设备像素比由面板直接提供，不必在代码里假设 DPI。
        /// </summary>
        internal int AdjustWidthForCss(int currentCss, int targetCss, double devicePixelRatio)
        {
            try
            {
                var hostWidth = Width;
                if (hostWidth <= 0 || currentCss <= 0)
                {
                    return -1;
                }

                // 不超过屏幕的一半，避免把工作表挤到无法使用。
                var maxWidth = (int)(System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Width * 0.5);
                var desired = PaneWidthMath.HostWidthForCss(
                    hostWidth, currentCss, targetCss, devicePixelRatio, maxWidth);

                if (desired <= hostWidth)
                {
                    // 已经够宽：仍然记下当前宽度，下次打开就不必再校准。
                    Persist(hostWidth);
                    return hostWidth;
                }

                Width = desired;
                var applied = Width;
                Persist(applied);
                Log.Info($"按面板请求校准宽度：CSS {currentCss}→{targetCss}，" +
                    $"宿主 {hostWidth}→{applied}（缩放 {devicePixelRatio:F2}）");
                return applied;
            }
            catch (Exception ex)
            {
                Log.Warn("宽度校准失败：" + ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// 记录用户手动拖动后的宽度，供下次打开直接恢复。
        /// 由面板在拖动停止后调用，因此这里只读当前值并存档。
        /// </summary>
        internal int PersistCurrentWidth()
        {
            var current = Width;
            if (current > 0)
            {
                Persist(current);
            }

            return current;
        }

        /// <summary>
        /// 打开面板时恢复记录过的宽度。
        ///
        /// 每次打开最多写一次宽度，这是不抽动的关键：
        /// 之前的做法是先盲写一个宿主单位下限，面板加载后再按视口反推一次，
        /// 显示瞬间连改两次宽度，看起来就是面板自己抽动了一下，
        /// 而且第二次反推依赖一瞬间的测量值，每次落点都不同。
        ///
        /// 没有记录时这里什么都不做，交给面板校准一次（见 pane.ensureWidth），
        /// 那条路径知道真实的 CSS 宽度与缩放比，一次就能定到位。
        /// </summary>
        private void RestoreWidth()
        {
            try
            {
                var stored = LoadStoredWidth();
                if (stored <= 0)
                {
                    return;
                }

                if (!Com.TryGet(_pane, "Width", out var raw) || raw == null)
                {
                    return;
                }

                var current = Convert.ToInt32(raw);
                if (current == stored)
                {
                    return;
                }

                Com.Set(_pane, "Width", stored);
                Log.Info($"侧边栏宽度由 {current} 恢复为记录值 {stored}");
            }
            catch (Exception ex)
            {
                // 宽度调整失败不影响功能，界面已按窄栏优先设计。
                Log.Warn("恢复侧边栏宽度失败：" + ex.Message);
            }
        }

        private static int LoadStoredWidth()
        {
            try
            {
                return Storage.Settings.Load().PaneWidth;
            }
            catch (Exception ex)
            {
                Log.Warn("读取记录的面板宽度失败：" + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// 存档宽度。只在数值真的变化时落盘，避免每次拖动都重写设置文件。
        /// </summary>
        private static void Persist(int width)
        {
            try
            {
                var settings = Storage.Settings.Load();
                if (settings.PaneWidth == width)
                {
                    return;
                }

                settings.PaneWidth = width;
                settings.Save();
            }
            catch (Exception ex)
            {
                // 记不住宽度只是下次要重新校准，不影响本次使用。
                Log.Warn("记录面板宽度失败：" + ex.Message);
            }
        }

        internal void Navigate(string route)
        {
            try
            {
                _control?.NavigateTo(route);
            }
            catch (Exception ex)
            {
                Log.Error($"面板导航到 {route} 失败", ex);
            }
        }

        internal string SendChat(string text)
        {
            try
            {
                return _control?.SendChatText(text) ?? "面板控件不可用";
            }
            catch (Exception ex)
            {
                Log.Error("投递测试消息失败", ex);
                return "失败：" + ex.Message;
            }
        }

        internal string ClickApproval(bool approve)
        {
            try
            {
                return _control?.ClickApprovalButton(approve) ?? "面板控件不可用";
            }
            catch (Exception ex)
            {
                Log.Error("点击审批按钮失败", ex);
                return "失败：" + ex.Message;
            }
        }

        internal string DrivePicker(string action)
        {
            try
            {
                return _control?.DrivePicker(action) ?? "面板控件不可用";
            }
            catch (Exception ex)
            {
                Log.Error("驱动选择器失败", ex);
                return "失败：" + ex.Message;
            }
        }

        internal string ReadElementText(string elementId)
        {
            try
            {
                return _control?.ReadElementText(elementId) ?? "面板控件不可用";
            }
            catch (Exception ex)
            {
                Log.Error("读取元素文本失败", ex);
                return "失败：" + ex.Message;
            }
        }

        internal string ClickUndo(int index)
        {
            try
            {
                return _control?.ClickUndoButton(index) ?? "面板控件不可用";
            }
            catch (Exception ex)
            {
                Log.Error("点击撤销按钮失败", ex);
                return "失败：" + ex.Message;
            }
        }

        internal string AttachImage(string dataUrl, string name)
        {
            try
            {
                return _control?.AttachImage(dataUrl, name) ?? "面板控件不可用";
            }
            catch (Exception ex)
            {
                Log.Error("附加图片失败", ex);
                return "失败：" + ex.Message;
            }
        }

        private static void TrySet(object target, string name, object value)
        {
            try
            {
                Com.Set(target, name, value);
            }
            catch (Exception ex)
            {
                // 两个宿主对停靠和宽度的支持程度不同，失败只降级不中断。
                Log.Warn($"设置窗格 {name} 失败：{ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                if (_pane != null)
                {
                    TrySet(_pane, "Visible", false);
                }
            }
            catch
            {
            }
            finally
            {
                Com.Release(_pane);
                _pane = null;
                _control = null;
            }
        }
    }
}
