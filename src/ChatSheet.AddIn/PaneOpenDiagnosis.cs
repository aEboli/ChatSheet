using System;

namespace ChatSheet.AddIn
{
    /// <summary>面板打不开的成因。按「用户能做什么」分类，不按代码里在哪一步失败分。</summary>
    internal enum PaneBlocker
    {
        /// <summary>没有失败。</summary>
        None = 0,

        /// <summary>只开着受保护的视图，宿主没有可承载面板的文档窗口。</summary>
        ProtectedView = 1,

        /// <summary>宿主里没有任何文档窗口（全部工作簿都已关闭）。</summary>
        NoDocumentWindow = 2,

        /// <summary>宿主正忙或有模态对话框，探测本身就被拒绝。</summary>
        HostBusy = 3,

        /// <summary>窗格工厂从未送达，通常是加载项被宿主拉黑或注册不完整。</summary>
        FactoryMissing = 4,

        /// <summary>窗格创建失败，通常是 ActiveX 控件没注册进两个 Classes 视图。</summary>
        CreateFailed = 5,

        /// <summary>窗格建成但显示被宿主拒绝，且重建后仍未成功。</summary>
        ShowRejected = 6,
    }

    /// <summary>
    /// 宿主窗口状态快照。只存探测结果，不持有任何 COM 对象，
    /// 这样成因判定就能在没有 Excel 的环境里直接验证。
    /// </summary>
    internal struct HostWindowState
    {
        /// <summary>普通文档窗口数。受保护的视图不计入，宿主本身就不把它算进 Windows。</summary>
        internal int DocumentWindows { get; set; }

        /// <summary>受保护的视图窗口数。</summary>
        internal int ProtectedViewWindows { get; set; }

        /// <summary>探测过程中宿主拒绝应答（忙或有模态对话框）。</summary>
        internal bool ProbeRejected { get; set; }
    }

    /// <summary>
    /// 面板打不开时的成因判定与文案。
    ///
    /// 单独成类是为了能直接验证：判定依赖的窗口状态只有在真实宿主里才存在，
    /// 混在窗格代码里就只能靠手工复现——而复现「只开着受保护的视图」
    /// 这类状态本身就很费事。
    /// </summary>
    internal static class PaneOpenDiagnosis
    {
        /// <summary>
        /// 判定成因。
        ///
        /// 顺序即优先级：先看宿主有没有能承载面板的窗口，再看加载项自身的状态。
        /// 反过来会把「没开工作簿」误报成注册问题，让用户白重装一遍。
        ///
        /// 只读工作簿不在此列，这是刻意的：只读工作簿是 Workbooks 的正常成员，
        /// 有真实的 Window，面板照常能开。把只读当成因会误导用户去改文件属性，
        /// 而真正拦住面板的是「没有文档窗口」——受保护的视图恰好同时是只读的，
        /// 两者容易被看成一回事。
        /// </summary>
        /// <param name="state">宿主窗口状态。</param>
        /// <param name="factoryReady">窗格工厂是否已送达。</param>
        /// <param name="createAttempted">是否已尝试过创建窗格。</param>
        /// <param name="createSucceeded">创建是否成功。</param>
        /// <param name="showSucceeded">显示是否成功（含重建后的重试）。</param>
        internal static PaneBlocker Classify(
            HostWindowState state,
            bool factoryReady,
            bool createAttempted,
            bool createSucceeded,
            bool showSucceeded)
        {
            // 宿主没有文档窗口是最硬的前提：窗格要挂在文档窗口上，
            // 没有窗口时无论加载项多正常都开不出来。
            if (state.DocumentWindows <= 0)
            {
                if (state.ProtectedViewWindows > 0)
                {
                    return PaneBlocker.ProtectedView;
                }

                // 探测被拒时不能断言「没有窗口」：读不到不等于没有。
                return state.ProbeRejected ? PaneBlocker.HostBusy : PaneBlocker.NoDocumentWindow;
            }

            if (!factoryReady)
            {
                return PaneBlocker.FactoryMissing;
            }

            if (createAttempted && !createSucceeded)
            {
                return PaneBlocker.CreateFailed;
            }

            if (!showSucceeded)
            {
                return PaneBlocker.ShowRejected;
            }

            return PaneBlocker.None;
        }

        /// <summary>
        /// 成因对应的提示文案。
        ///
        /// 形状沿用面板内兜底文案那一套：一句话说清发生了什么 →
        /// 括号里补最常见的触发条件 → 最后一句交代还能做什么。
        /// 不逐一枚举内部失败步骤，具体差别记在日志里。
        /// </summary>
        internal static string Describe(PaneBlocker blocker)
        {
            switch (blocker)
            {
                case PaneBlocker.ProtectedView:
                    return "当前文件在「受保护的视图」里打开，面板没有可以停靠的窗口，" +
                        "所以点了没有反应（从邮件附件、网页下载或网络位置打开的文件最常见）。" +
                        "请点表格上方黄色提示条里的「启用编辑」，然后重新点面板。";

                case PaneBlocker.NoDocumentWindow:
                    return "当前没有打开的工作簿，面板没有可以停靠的窗口，所以点了没有反应。" +
                        "请先新建或打开一个工作簿，再点面板。";

                case PaneBlocker.HostBusy:
                    return "Excel 正忙，没有应答面板的请求（正在打开文件，或有对话框没关掉时最常见）。" +
                        "请先关掉表格上的对话框、等当前操作结束，然后重新点面板。";

                case PaneBlocker.FactoryMissing:
                    return "Excel 没有给本加载项开放建立面板所需的接口，面板建不起来" +
                        "（加载项曾经加载失败被 Excel 拉黑时最常见）。" +
                        "请完全退出 Excel 后重新打开；仍然不行就重新运行一次 install.bat。";

                case PaneBlocker.CreateFailed:
                    return "面板控件没能创建出来，通常是控件注册不完整" +
                        "（换过安装位置或只装了一半时最常见）。" +
                        "请重新运行一次 install.bat，然后完全退出 Excel 再打开。";

                case PaneBlocker.ShowRejected:
                    return "Excel 拒绝显示面板，重建之后仍然没有成功。" +
                        "请完全退出 Excel 后重新打开；如果每次都这样，请把日志一起反馈。";

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// 拼出完整的弹窗正文：成因 + 空行 + 日志位置。
        /// 日志路径解析失败时退回环境变量写法，不能给出一个空的「日志：」。
        /// </summary>
        internal static string Compose(PaneBlocker blocker, string logPath)
        {
            var reason = Describe(blocker);
            if (string.IsNullOrEmpty(reason))
            {
                return string.Empty;
            }

            var where = string.IsNullOrEmpty(logPath)
                ? @"%LOCALAPPDATA%\ChatSheet\logs"
                : logPath;

            return reason + Environment.NewLine + Environment.NewLine + "日志：" + where;
        }
    }
}
