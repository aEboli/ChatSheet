using System;
using ChatSheet.AddIn;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 面板打不开时的成因判定验证。
    ///
    /// 用例取自真实日志：2026-08-31 11:26 窗格随工作簿窗口一起被销毁
    /// （日志里只留下「焦点守卫已卸载」），11:28 起每次点击都抛
    /// HRESULT 0x800A01A8，而加载项一侧毫无反应也毫无提示。
    ///
    /// 只读工作簿单独立了一条用例：只读是 Workbooks 的正常成员、有真实窗口，
    /// 面板照常能开。把只读判成成因会让用户去改文件属性，而真正拦住面板的是
    /// 「没有文档窗口」——受保护的视图恰好同时是只读的，两者极易混为一谈。
    /// </summary>
    internal static class PaneOpenDiagnosisTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestWindowStateTakesPrecedence(report);
            TestReadOnlyIsNotABlocker(report);
            TestAddInSideBlockers(report);
            TestProbeRejected(report);
            TestMessages(report);
        }

        private static HostWindowState State(int documents, int protectedViews, bool rejected = false)
        {
            return new HostWindowState
            {
                DocumentWindows = documents,
                ProtectedViewWindows = protectedViews,
                ProbeRejected = rejected,
            };
        }

        private static void TestWindowStateTakesPrecedence(Action<string, bool, string> report)
        {
            // 只开着受保护的视图：宿主不把它算进 Windows，所以没有可停靠的窗口。
            var pv = PaneOpenDiagnosis.Classify(
                State(documents: 0, protectedViews: 1),
                factoryReady: true,
                createAttempted: true,
                createSucceeded: false,
                showSucceeded: false);
            report(
                "只开着受保护的视图判为受保护的视图",
                pv == PaneBlocker.ProtectedView,
                pv.ToString());

            // 全部工作簿都关掉，Excel 还在：同样没有窗口，但成因不同，建议也不同。
            var none = PaneOpenDiagnosis.Classify(
                State(documents: 0, protectedViews: 0),
                factoryReady: true,
                createAttempted: true,
                createSucceeded: false,
                showSucceeded: false);
            report(
                "没有任何工作簿判为没有文档窗口",
                none == PaneBlocker.NoDocumentWindow,
                none.ToString());

            // 窗口状态优先于加载项自身状态：没有窗口时即使工厂也缺，
            // 也不能让用户去重装——重装解决不了「没开工作簿」。
            var bothBad = PaneOpenDiagnosis.Classify(
                State(documents: 0, protectedViews: 0),
                factoryReady: false,
                createAttempted: true,
                createSucceeded: false,
                showSucceeded: false);
            report(
                "没有窗口时不误报成注册问题",
                bothBad == PaneBlocker.NoDocumentWindow,
                bothBad.ToString());

            // 受保护的视图与普通工作簿同时开着：能停靠，不该判成受保护的视图。
            var mixed = PaneOpenDiagnosis.Classify(
                State(documents: 1, protectedViews: 1),
                factoryReady: true,
                createAttempted: false,
                createSucceeded: true,
                showSucceeded: false);
            report(
                "受保护的视图与普通工作簿并存时不判为受保护的视图",
                mixed == PaneBlocker.ShowRejected,
                mixed.ToString());
        }

        private static void TestReadOnlyIsNotABlocker(Action<string, bool, string> report)
        {
            // 只读工作簿在状态里与可写工作簿完全一样：一个文档窗口。
            // 判定里没有只读这个维度，因此结果必须是「没有成因」。
            var readOnly = PaneOpenDiagnosis.Classify(
                State(documents: 1, protectedViews: 0),
                factoryReady: true,
                createAttempted: true,
                createSucceeded: true,
                showSucceeded: true);
            report(
                "只读工作簿不构成打不开的成因",
                readOnly == PaneBlocker.None,
                readOnly.ToString());

            report(
                "没有成因时不出文案",
                PaneOpenDiagnosis.Describe(PaneBlocker.None) == string.Empty &&
                    PaneOpenDiagnosis.Compose(PaneBlocker.None, @"C:\x.log") == string.Empty,
                "");
        }

        private static void TestAddInSideBlockers(Action<string, bool, string> report)
        {
            var factory = PaneOpenDiagnosis.Classify(
                State(documents: 1, protectedViews: 0),
                factoryReady: false,
                createAttempted: true,
                createSucceeded: false,
                showSucceeded: false);
            report(
                "有窗口但工厂未送达判为工厂缺失",
                factory == PaneBlocker.FactoryMissing,
                factory.ToString());

            var create = PaneOpenDiagnosis.Classify(
                State(documents: 1, protectedViews: 0),
                factoryReady: true,
                createAttempted: true,
                createSucceeded: false,
                showSucceeded: false);
            report(
                "工厂在但创建失败判为创建失败",
                create == PaneBlocker.CreateFailed,
                create.ToString());

            // 日志里的真实场景：窗格重建成功了，但宿主仍然不显示。
            var rejected = PaneOpenDiagnosis.Classify(
                State(documents: 1, protectedViews: 0),
                factoryReady: true,
                createAttempted: true,
                createSucceeded: true,
                showSucceeded: false);
            report(
                "创建成功但显示被拒判为显示被拒",
                rejected == PaneBlocker.ShowRejected,
                rejected.ToString());

            // 没尝试创建（窗格本来就在）而显示失败，也要落到显示被拒，
            // 不能因为 createSucceeded 的默认值把它算成创建失败。
            var existing = PaneOpenDiagnosis.Classify(
                State(documents: 1, protectedViews: 0),
                factoryReady: true,
                createAttempted: false,
                createSucceeded: false,
                showSucceeded: false);
            report(
                "未尝试创建时不判为创建失败",
                existing == PaneBlocker.ShowRejected,
                existing.ToString());
        }

        private static void TestProbeRejected(Action<string, bool, string> report)
        {
            // 探测被拒时读到的 0 不可信，不能断言「没有工作簿」。
            var busy = PaneOpenDiagnosis.Classify(
                State(documents: 0, protectedViews: 0, rejected: true),
                factoryReady: true,
                createAttempted: true,
                createSucceeded: false,
                showSucceeded: false);
            report(
                "探测被拒判为宿主忙",
                busy == PaneBlocker.HostBusy,
                busy.ToString());

            // 但受保护的视图数读到了就以它为准：这条信息本身已经够确定。
            var busyButPv = PaneOpenDiagnosis.Classify(
                State(documents: 0, protectedViews: 2, rejected: true),
                factoryReady: true,
                createAttempted: true,
                createSucceeded: false,
                showSucceeded: false);
            report(
                "探测被拒但确知有受保护的视图时以后者为准",
                busyButPv == PaneBlocker.ProtectedView,
                busyButPv.ToString());
        }

        private static void TestMessages(Action<string, bool, string> report)
        {
            // 每种成因都必须有文案，且必须带一个用户能做的动作。
            var blockers = new[]
            {
                PaneBlocker.ProtectedView,
                PaneBlocker.NoDocumentWindow,
                PaneBlocker.HostBusy,
                PaneBlocker.FactoryMissing,
                PaneBlocker.CreateFailed,
                PaneBlocker.ShowRejected,
            };

            foreach (var blocker in blockers)
            {
                var text = PaneOpenDiagnosis.Describe(blocker);
                report(
                    $"{blocker} 有文案且给出动作",
                    !string.IsNullOrEmpty(text) && text.Contains("请"),
                    text);
            }

            // 文案里不能出现只对开发者有意义的词。
            foreach (var blocker in blockers)
            {
                var text = PaneOpenDiagnosis.Describe(blocker);
                report(
                    $"{blocker} 文案不含内部术语",
                    !text.Contains("窗格") && !text.Contains("CreateCTP") && !text.Contains("COM"),
                    text);
            }

            // 受保护的视图那条必须点出「启用编辑」，这是用户唯一能做的动作。
            report(
                "受保护的视图文案指向启用编辑",
                PaneOpenDiagnosis.Describe(PaneBlocker.ProtectedView).Contains("启用编辑"),
                "");

            // 拼装：正文 + 空行 + 日志路径。
            var composed = PaneOpenDiagnosis.Compose(PaneBlocker.NoDocumentWindow, @"C:\logs\addin-EXCEL.log");
            report(
                "拼装带上日志路径",
                composed.Contains(@"C:\logs\addin-EXCEL.log") && composed.Contains("日志："),
                composed);

            // 日志路径解析失败时不能留一个空的「日志：」。
            var fallback = PaneOpenDiagnosis.Compose(PaneBlocker.NoDocumentWindow, string.Empty);
            report(
                "无日志路径时退回环境变量写法",
                fallback.Contains(@"%LOCALAPPDATA%\ChatSheet\logs"),
                fallback);
        }
    }
}
