using System;
using System.Runtime.InteropServices;

namespace ChatSheet.AddIn
{
    /// <summary>
    /// 加载项对外的自动化接口。
    /// 通过 Application.COMAddIns("ChatSheet.AddIn").Object 取得，
    /// 可供 VBA 或自动化脚本控制面板，也是自动化验证面板行为的唯一途径
    /// （功能区按钮只能由真实点击触发）。
    /// </summary>
    [ComVisible(true)]
    [Guid("6C4E5B21-9F3A-4D7E-8C1B-2A6D0F94E5C7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IAddInAutomation
    {
        /// <summary>显示面板并切到指定页：chat、settings 或 diagnostics。</summary>
        void ShowPane(string route);

        /// <summary>隐藏面板。</summary>
        void HidePane();

        /// <summary>面板当前是否可见。</summary>
        bool IsPaneVisible { get; }

        /// <summary>日志文件路径，便于排查。</summary>
        string LogPath { get; }

        /// <summary>宿主报告的面板宽度。用于核对宽度设置是否真正生效。</summary>
        int PaneWidth { get; }

        /// <summary>设置面板宽度，返回宿主实际采用的值。</summary>
        int SetPaneWidth(int width);

        /// <summary>
        /// 把文本填入输入框并触发发送，走与用户点击完全相同的路径。
        /// 供端到端验证使用，也可供 VBA 脚本化调用。
        /// </summary>
        string SendChatForTest(string text);

        /// <summary>
        /// 点击当前待处理的审批卡片。approve 为真点「允许」，为假点「拒绝」。
        /// 返回是否找到并点击了卡片。供端到端验证审批链路使用。
        /// </summary>
        string ClickApprovalForTest(bool approve);

        /// <summary>
        /// 驱动模型/思考等级选择器，供端到端验证使用。
        /// action 取 open、close、models、thinkings、state、favorites、
        /// toggle-only-favorites，或带冒号的 pick-model:&lt;名称&gt;、
        /// pick-thinking:&lt;档位&gt;、star:&lt;名称&gt;、probe:&lt;名称&gt;、
        /// verdict:&lt;名称&gt;、probe-visible:&lt;名称&gt;、thinking-row:&lt;档位&gt;、
        /// manual:&lt;名称&gt;。
        /// </summary>
        string DrivePickerForTest(string action);

        /// <summary>
        /// 驱动并测量进场动画与顶栏图标的点击回弹，供端到端验证使用。
        /// action 取 reset、mount、remount、card、move-card-away、card-state、
        /// state，或带冒号的 tap:&lt;chat|settings|theme&gt;、
        /// tap-twice:&lt;chat|settings|theme&gt;。
        ///
        /// 这些结论只有真实渲染器算得出来：动画此刻在跑没跑、播到第几毫秒，
        /// 假 DOM 与 CSS 静态检查都看不到。
        /// </summary>
        string DriveMotionForTest(string action);

        /// <summary>
        /// 读取面板中指定元素的文本，供端到端验证界面内容。
        /// </summary>
        string ReadElementTextForTest(string elementId);

        /// <summary>
        /// 读取输入框内容与选中范围，形如 value|选中起-选中止。
        /// 输入框是 textarea，用户键入的内容在 value 上，
        /// ReadElementTextForTest 读的 textContent 恒为空，无法用来验证键盘输入。
        /// 选中范围用于验证面板内的 Ctrl+A 仍然选中输入框里的文字。
        /// </summary>
        string ReadComposerTextForTest();

        /// <summary>
        /// 点击操作卡片上的撤销/恢复按钮。
        /// index 从 0 起，指第几个可撤销的操作卡片。
        /// 返回点击前按钮的文字，便于判断本次是撤销还是恢复。
        /// </summary>
        string ClickUndoForTest(int index);

        /// <summary>
        /// 附加一张图片到输入区，供端到端验证多模态链路。
        /// dataUrl 为 data:image/...;base64,... 形式。
        /// </summary>
        string AttachImageForTest(string dataUrl, string name);

        /// <summary>
        /// 读取输入队列状态，形如
        /// 排队=2 | 已发送=1 | 按钮=加入队列 | 输入框可用=True |
        /// 排队条可见=True | 排队条可滑动=False | 位次=1，2 | 排队内容=… 。
        /// 处理中仍可继续输入，新输入会排队，因此验证要能看到队列本身。
        /// </summary>
        string ReadQueueForTest();

        /// <summary>取消第 index 条排队中的输入，index 从 0 起。</summary>
        string CancelQueuedForTest(int index);

        /// <summary>
        /// 直接点发送按钮，不预先填字。
        /// 输入框为空且正在处理时，该按钮的含义是停止，这是唯一能走到那条路径的入口。
        /// </summary>
        string ClickSendForTest();

        /// <summary>
        /// 点击「适配」浮层里的对齐选项，alignment 取 left/center/right。
        /// 走真实点击，因此覆盖从按钮到撤销入口的完整路径。
        /// </summary>
        string ClickFitForTest(string alignment);

        /// <summary>点主题切换按钮，返回切换后的主题（light 或 dark）。</summary>
        string ClickThemeToggleForTest();

        /// <summary>
        /// 读取当前主题与几处关键元素的实际计算颜色，用于核对深浅两套配色
        /// 是否真的生效。返回 theme=…|scheme=…|body=…|text=…|bar=…|composer=…|send=…|glyph=…
        /// </summary>
        string ReadThemeStateForTest();

        /// <summary>
        /// 读取最后一条提示胶囊，形如 文字=… | 撤销入口=撤销 。
        /// </summary>
        string ReadLastNoticeForTest();

        /// <summary>
        /// 读取最后一张工具操作卡片，形如
        /// 名称=适配 | 来源=手动 | 状态=… | 撤销入口=撤销 。
        /// 面板直接发起的操作（适配）与模型发起的用同一种卡片，靠「来源」区分。
        /// </summary>
        string ReadLastToolCardForTest();

        /// <summary>
        /// 读取轮次操作组状态，形如
        /// 组数=1 | 组外卡片=2 | 组1=第 1 轮 3 个操作（2 改，1 读）/卡片3/收起/无失败/还原入口有 。
        /// 一轮的操作在下一轮开始时收成一组，只数 .tool-card 看不出收没收——
        /// 卡片进了组仍在 DOM 里。
        /// </summary>
        string ReadOperationGroupsForTest();

        /// <summary>
        /// 点第 index 个轮次操作组上的「还原」按钮，index 从 0 起。
        /// 还原后卡片回到对话流原位，组解散。
        /// </summary>
        string ClickRestoreOperationGroupForTest(int index);
    }

    [ComVisible(true)]
    [Guid("B8D3F1A6-7E24-4C95-A03D-5F1E8B6C2A94")]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IAddInAutomation))]
    public sealed class AddInAutomation : IAddInAutomation
    {
        private readonly ComAddIn _owner;

        internal AddInAutomation(ComAddIn owner)
        {
            _owner = owner;
        }

        public void ShowPane(string route)
        {
            try
            {
                _owner.ShowPaneForAutomation(string.IsNullOrWhiteSpace(route) ? "chat" : route);
            }
            catch (Exception ex)
            {
                // 自动化调用方通常是脚本，异常要能被它看到，但先记录以便排查。
                Log.Error("自动化 ShowPane 失败", ex);
                throw;
            }
        }

        public void HidePane()
        {
            try
            {
                _owner.HidePaneForAutomation();
            }
            catch (Exception ex)
            {
                Log.Error("自动化 HidePane 失败", ex);
                throw;
            }
        }

        public bool IsPaneVisible
        {
            get
            {
                try
                {
                    return _owner.IsPaneVisibleForAutomation();
                }
                catch
                {
                    return false;
                }
            }
        }

        public string LogPath => Log.CurrentPath;

        public int PaneWidth
        {
            get
            {
                try
                {
                    return _owner.GetPaneWidthForAutomation();
                }
                catch
                {
                    return -1;
                }
            }
        }

        public int SetPaneWidth(int width)
        {
            try
            {
                return _owner.SetPaneWidthForAutomation(width);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 SetPaneWidth 失败", ex);
                throw;
            }
        }

        public string SendChatForTest(string text)
        {
            try
            {
                return _owner.SendChatForAutomation(text);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 SendChatForTest 失败", ex);
                throw;
            }
        }

        public string ClickApprovalForTest(bool approve)
        {
            try
            {
                return _owner.ClickApprovalForAutomation(approve);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ClickApprovalForTest 失败", ex);
                throw;
            }
        }

        public string DrivePickerForTest(string action)
        {
            try
            {
                return _owner.DrivePickerForAutomation(action);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 DrivePickerForTest 失败", ex);
                throw;
            }
        }

        public string DriveMotionForTest(string action)
        {
            try
            {
                return _owner.DriveMotionForAutomation(action);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 DriveMotionForTest 失败", ex);
                throw;
            }
        }

        public string ReadElementTextForTest(string elementId)
        {
            try
            {
                return _owner.ReadElementTextForAutomation(elementId);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ReadElementTextForTest 失败", ex);
                throw;
            }
        }

        public string ReadComposerTextForTest()
        {
            try
            {
                return _owner.ReadComposerTextForAutomation();
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ReadComposerTextForTest 失败", ex);
                throw;
            }
        }

        public string ClickUndoForTest(int index)
        {
            try
            {
                return _owner.ClickUndoForAutomation(index);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ClickUndoForTest 失败", ex);
                throw;
            }
        }

        public string AttachImageForTest(string dataUrl, string name)
        {
            try
            {
                return _owner.AttachImageForAutomation(dataUrl, name);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 AttachImageForTest 失败", ex);
                throw;
            }
        }

        public string ReadQueueForTest()
        {
            try
            {
                return _owner.ReadQueueForAutomation();
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ReadQueueForTest 失败", ex);
                throw;
            }
        }

        public string CancelQueuedForTest(int index)
        {
            try
            {
                return _owner.CancelQueuedForAutomation(index);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 CancelQueuedForTest 失败", ex);
                throw;
            }
        }

        public string ClickSendForTest()
        {
            try
            {
                return _owner.ClickSendForAutomation();
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ClickSendForTest 失败", ex);
                throw;
            }
        }

        public string ClickFitForTest(string alignment)
        {
            try
            {
                return _owner.ClickFitForAutomation(alignment);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ClickFitForTest 失败", ex);
                throw;
            }
        }

        public string ClickThemeToggleForTest()
        {
            try
            {
                return _owner.ClickThemeToggleForAutomation();
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ClickThemeToggleForTest 失败", ex);
                throw;
            }
        }

        public string ReadThemeStateForTest()
        {
            try
            {
                return _owner.ReadThemeStateForAutomation();
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ReadThemeStateForTest 失败", ex);
                throw;
            }
        }

        public string ReadLastNoticeForTest()
        {
            try
            {
                return _owner.ReadLastNoticeForAutomation();
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ReadLastNoticeForTest 失败", ex);
                throw;
            }
        }

        public string ReadLastToolCardForTest()
        {
            try
            {
                return _owner.ReadLastToolCardForAutomation();
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ReadLastToolCardForTest 失败", ex);
                throw;
            }
        }

        public string ReadOperationGroupsForTest()
        {
            try
            {
                return _owner.ReadOperationGroupsForAutomation();
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ReadOperationGroupsForTest 失败", ex);
                throw;
            }
        }

        public string ClickRestoreOperationGroupForTest(int index)
        {
            try
            {
                return _owner.ClickRestoreOperationGroupForAutomation(index);
            }
            catch (Exception ex)
            {
                Log.Error("自动化 ClickRestoreOperationGroupForTest 失败", ex);
                throw;
            }
        }
    }
}
