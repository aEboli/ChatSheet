using System;
using ChatSheet.AddIn;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 面板宽度换算验证。
    ///
    /// 用例里的数字取自真实运行日志：150% 缩放下宿主 626 对应 407 CSS 像素。
    /// 旧实现按「宿主 ÷ CSS」求比例，测量稍有偏差就整体放大，
    /// 曾把目标 400 CSS 拉到 452，表现为面板自己抽动一下再定在错误宽度。
    /// </summary>
    internal static class PaneWidthTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestTargetHit(report);
            TestStaleMeasurementTolerance(report);
            TestClamp(report);
            TestScaleGuard(report);
        }

        private static void TestTargetHit(Action<string, bool, string> report)
        {
            // 实测对应关系：宿主 626 ↔ 407 CSS，缩放 1.5。
            // 要 400 CSS 就该略微收窄，而不是继续加宽。
            var desired = PaneWidthMath.HostWidthForCss(626, 407, 400, 1.5, 2000);
            report(
                "已够宽时不再加宽",
                desired <= 626,
                $"得到 {desired}，不应超过 626");

            // 从 257 CSS 拉到 400 CSS：差 143 CSS，按 1.5 缩放约需多 215 宿主单位。
            var widened = PaneWidthMath.HostWidthForCss(401, 257, 400, 1.5, 2000);
            report(
                "按增量换算加宽量",
                widened >= 615 && widened <= 620,
                $"得到 {widened}，预期 616 上下");

            // 换算完再反推回 CSS，应落在目标附近，而不是超出一大截。
            var backToCss = 257 + (int)Math.Round((widened - 401) / 1.5);
            report(
                "换算结果回推接近目标",
                Math.Abs(backToCss - 400) <= 2,
                $"回推 {backToCss} CSS，目标 400");
        }

        private static void TestStaleMeasurementTolerance(Action<string, bool, string> report)
        {
            // 日志里的翻车场景：宿主已是 585，但面板报的仍是过渡值 340
            // （真实约 382）。旧的比例法据此算出 689，最终得到 452 CSS。
            // 增量法即使吃到同样的过渡值，也只会偏出这段测量误差本身，不会被放大。
            var desired = PaneWidthMath.HostWidthForCss(585, 340, 400, 1.5, 2000);
            var actualCss = 382 + (int)Math.Round((desired - 585) / 1.5);

            report(
                "过渡测量下的偏差不被放大",
                Math.Abs(actualCss - 400) <= 45,
                $"实际得到约 {actualCss} CSS，旧实现为 452");

            // 与旧比例法对比，必须明显更接近目标。
            var oldWay = (int)Math.Ceiling(400 * (585.0 / 340));
            var oldCss = 382 + (int)Math.Round((oldWay - 585) / 1.5);
            report(
                "优于旧比例法",
                Math.Abs(actualCss - 400) < Math.Abs(oldCss - 400),
                $"新法 {actualCss} CSS，旧法 {oldCss} CSS");
        }

        private static void TestClamp(Action<string, bool, string> report)
        {
            // 上限保护：不能把工作表挤到无法使用。
            var clamped = PaneWidthMath.HostWidthForCss(400, 100, 4000, 2.0, 900);
            report("受上限约束", clamped == 900, clamped.ToString());

            // 上限为非正数表示不限制。
            var unclamped = PaneWidthMath.HostWidthForCss(400, 200, 300, 1.0, 0);
            report("上限非正数表示不限制", unclamped == 500, unclamped.ToString());

            // 宿主宽度或 CSS 宽度不可用时原样返回，不要凭空算一个值。
            report(
                "无效输入原样返回",
                PaneWidthMath.HostWidthForCss(-1, 300, 400, 1.5, 2000) == -1 &&
                    PaneWidthMath.HostWidthForCss(400, 0, 400, 1.5, 2000) == 400,
                "");
        }

        private static void TestScaleGuard(Action<string, bool, string> report)
        {
            report("正常缩放原样采用", Math.Abs(PaneWidthMath.Scale(1.5) - 1.5) < 0.0001, "");
            report("缩放为 0 时退回 1", Math.Abs(PaneWidthMath.Scale(0) - 1.0) < 0.0001, "");
            report("缩放离谱时退回 1", Math.Abs(PaneWidthMath.Scale(99) - 1.0) < 0.0001, "");
            report("缩放为负时退回 1", Math.Abs(PaneWidthMath.Scale(-2) - 1.0) < 0.0001, "");
        }
    }
}
