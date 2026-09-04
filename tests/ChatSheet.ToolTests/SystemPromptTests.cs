using System;
using ChatSheet.AddIn.Agent;
using ChatSheet.AddIn.Hosts;
using ChatSheet.AddIn.Providers;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 系统提示里的当前时间验证。
    ///
    /// 模型没有时钟，「日期写今天」会得到一个训练数据截止前后的日期：
    /// 形态合法、不报错、也不会有任何工具返回值来纠正它，
    /// 用户不逐格核对就发现不了。这一段是唯一的纠正来源，
    /// 因此要钉住时刻逐项验证渲染结果，而不是只看「有没有这一段」。
    /// </summary>
    internal static class SystemPromptTests
    {
        // 2026-09-04 是星期五。星期名写死在实现里，只有钉住已知的一天才验得到映射对不对。
        private static readonly DateTimeOffset Pinned =
            new DateTimeOffset(2026, 9, 4, 14, 37, 52, TimeSpan.FromHours(8));

        internal static void Run(Action<string, bool, string> report)
        {
            TestRenderedMoment(report);
            TestWeekdayMapping(report);
            TestOffsetFormatting(report);
            TestGuidance(report);
            TestPresentInEveryToolMode(report);
            TestPlacement(report);
        }

        private static string Build(ToolProtocolMode mode, DateTimeOffset now)
        {
            var summary = new WorkbookSummary
            {
                HasWorkbook = true,
                Name = "测试.xlsx",
                SheetCount = 1,
                ActiveSheet = "Sheet1",
            };

            return SystemPrompt.Build(summary, null, false, mode, now);
        }

        private static void TestRenderedMoment(Action<string, bool, string> report)
        {
            var prompt = Build(ToolProtocolMode.Native, Pinned);

            report(
                "写出当天日期",
                prompt.Contains("2026-09-04"),
                "未找到 2026-09-04");

            report(
                "写出当前时刻",
                prompt.Contains("14:37"),
                "未找到 14:37");

            // 秒级没有用处，还会让每轮系统提示都不一样。
            report(
                "不写到秒",
                !prompt.Contains("14:37:52"),
                "带上了秒");
        }

        private static void TestWeekdayMapping(Action<string, bool, string> report)
        {
            report(
                "星期五写作星期五",
                Build(ToolProtocolMode.Native, Pinned).Contains("星期五"),
                "未找到星期五");

            // 索引 0 的边界：DayOfWeek.Sunday 是 0，映射错位时这一条先失败。
            var sunday = new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.FromHours(8));
            report(
                "星期日写作星期日",
                Build(ToolProtocolMode.Native, sunday).Contains("星期日"),
                "未找到星期日");

            // 区域设置不是中文时不能漏出英文星期名，否则模型会跟着换语言。
            report(
                "不写英文星期名",
                !Build(ToolProtocolMode.Native, Pinned).Contains("Friday"),
                "出现了 Friday");
        }

        private static void TestOffsetFormatting(Action<string, bool, string> report)
        {
            report(
                "东八区写作 UTC+08:00",
                Build(ToolProtocolMode.Native, Pinned).Contains("UTC+08:00"),
                "未找到 UTC+08:00");

            var west = new DateTimeOffset(2026, 9, 4, 2, 5, 0, TimeSpan.FromHours(-5));
            report(
                "西五区写作 UTC-05:00",
                Build(ToolProtocolMode.Native, west).Contains("UTC-05:00"),
                "未找到 UTC-05:00");

            // 半小时时区：分钟位补零写死成 :00 的话这一条会失败。
            var half = new DateTimeOffset(2026, 9, 4, 2, 5, 0, new TimeSpan(5, 30, 0));
            report(
                "半小时时区写出分钟",
                Build(ToolProtocolMode.Native, half).Contains("UTC+05:30"),
                "未找到 UTC+05:30");
        }

        private static void TestGuidance(Action<string, bool, string> report)
        {
            var prompt = Build(ToolProtocolMode.Native, Pinned);

            report(
                "写明这是唯一可信的时间来源",
                prompt.Contains("唯一可信的时间来源"),
                "缺少基准说明");

            report(
                "点名相对说法要按此推算",
                prompt.Contains("今天") && prompt.Contains("相对说法"),
                "缺少相对说法的处理方式");

            // 反过来的错也要挡：用户要的是固定值时不该塞一个每天都变的公式。
            report(
                "区分固定值与 TODAY 公式",
                prompt.Contains("=TODAY()") && prompt.Contains("固定值"),
                "未区分两种意图");
        }

        private static void TestPresentInEveryToolMode(Action<string, bool, string> report)
        {
            foreach (var mode in new[] { ToolProtocolMode.Native, ToolProtocolMode.Text, ToolProtocolMode.None })
            {
                var prompt = Build(mode, Pinned);
                report(
                    $"{mode} 形态带当前时间",
                    prompt.Contains("## 当前时间") && prompt.Contains("2026-09-04"),
                    "缺少当前时间段落");
            }
        }

        private static void TestPlacement(Action<string, bool, string> report)
        {
            var prompt = Build(ToolProtocolMode.Native, Pinned);
            var time = prompt.IndexOf("## 当前时间", StringComparison.Ordinal);
            var workbook = prompt.IndexOf("## 当前工作簿", StringComparison.Ordinal);

            report(
                "当前时间紧邻工作簿信息之前",
                time > 0 && workbook > time,
                $"时间在 {time}，工作簿在 {workbook}");
        }
    }
}
