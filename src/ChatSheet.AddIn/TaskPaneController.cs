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
                control?.AttachWidthAdjuster(controller.AdjustWidthForCss);
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
                        EnsureMinimumWidth();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("设置侧边栏可见性失败", ex);
                }
            }
        }

        /// <summary>
        /// 宿主 Width 属性的下限，单位与显示缩放相关，并非 CSS 像素。
        ///
        /// 实测：在 150% 缩放下 Width=401 只换来 257 CSS 像素的视口，
        /// 二者之比即缩放系数。因此这里只作为兜底下限，
        /// 真正的宽度校准由面板自行测量后请求（见 pane.ensureWidth 通道）——
        /// 那样能自动适配任意缩放比例，不必在代码里假设 DPI。
        /// </summary>
        private const int MinimumWidth = 400;

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
        /// 按面板报告的 CSS 宽度校准宿主宽度。
        ///
        /// 宿主 Width 的单位随显示缩放变化，无法在代码中假定换算系数；
        /// 用「当前宿主宽度 ÷ 当前 CSS 宽度」现算出比例，再乘目标 CSS 宽度，
        /// 即可在任意缩放下得到正确值。
        /// </summary>
        internal int AdjustWidthForCss(int currentCss, int targetCss)
        {
            try
            {
                var hostWidth = Width;
                if (hostWidth <= 0 || currentCss <= 0)
                {
                    return -1;
                }

                var ratio = (double)hostWidth / currentCss;
                var desired = (int)Math.Ceiling(targetCss * ratio);

                // 不超过屏幕的一半，避免把工作表挤到无法使用。
                var maxWidth = (int)(System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Width * 0.5);
                if (desired > maxWidth)
                {
                    desired = maxWidth;
                }

                if (desired <= hostWidth)
                {
                    return hostWidth;
                }

                Width = desired;
                var applied = Width;
                Log.Info($"按面板请求校准宽度：CSS {currentCss}→{targetCss}，宿主 {hostWidth}→{applied}（比例 {ratio:F2}）");
                return applied;
            }
            catch (Exception ex)
            {
                Log.Warn("宽度校准失败：" + ex.Message);
                return -1;
            }
        }

        private void EnsureMinimumWidth()
        {
            try
            {
                if (!Com.TryGet(_pane, "Width", out var raw) || raw == null)
                {
                    return;
                }

                var current = Convert.ToInt32(raw);
                if (current >= MinimumWidth)
                {
                    return;
                }

                Com.Set(_pane, "Width", MinimumWidth);
                Log.Info($"侧边栏宽度由 {current} 调整为 {MinimumWidth}");
            }
            catch (Exception ex)
            {
                // 宽度调整失败不影响功能，界面已按窄栏优先设计。
                Log.Warn("调整侧边栏宽度失败：" + ex.Message);
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
