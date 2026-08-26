using System;
using ChatSheet.AddIn.Agent;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 「任务做一半自己停住」的判定验证。
    ///
    /// 真实现象：开着高档思考时输出上限（8192）被推理吃光，服务端截断，
    /// 于是正文为空、也没有工具调用。旧逻辑只看「没有待执行的工具调用」
    /// 就判定本轮结束，用户必须再发一条消息才能接上。
    /// 这里覆盖的是判定本身——端到端很难稳定复现贴顶截断。
    /// </summary>
    internal static class StallTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestLengthFinishReasons(report);
            TestClassify(report);
            TestTruncatedJsonDetection(report);
        }

        private static void TestLengthFinishReasons(Action<string, bool, string> report)
        {
            // 各协议对「达到输出上限」的叫法都不同，缺一个就漏判一类服务端。
            foreach (var reason in new[] { "length", "max_tokens", "MAX_TOKENS", "response.incomplete", "incomplete" })
            {
                report(
                    $"结束原因 {reason} 判为截断",
                    TurnOutcome.IsLengthFinish(reason),
                    reason);
            }

            foreach (var reason in new[] { "stop", "tool_calls", "end_turn", "STOP", "response.completed", null, "" })
            {
                report(
                    $"结束原因 {reason ?? "<空>"} 不判为截断",
                    !TurnOutcome.IsLengthFinish(reason),
                    reason ?? "<空>");
            }
        }

        private static void TestClassify(Action<string, bool, string> report)
        {
            // 正常收尾：有正文、结束原因是 stop。
            report(
                "正常收尾不算停顿",
                TurnOutcome.Classify("stop", false, 120, 300, 8192) == StepStall.None,
                "");

            // 结束原因明说截断。
            report(
                "结束原因 length 判为截断",
                TurnOutcome.Classify("length", false, 0, 8192, 8192) == StepStall.Truncated,
                "");

            // 网关不回结束原因，只能靠用量贴顶识别。这正是实测日志里的情形：
            // 结束原因「未提供」，用量 8192 出，回复长度 0。
            report(
                "结束原因为空但用量贴顶时判为截断",
                TurnOutcome.Classify(null, false, 0, 8192, 8192) == StepStall.Truncated,
                "");

            // 留 2% 余量：服务端计数与请求上限常有个位数出入。
            report(
                "用量差 1% 仍判为截断",
                TurnOutcome.Classify(null, false, 0, 8120, 8192) == StepStall.Truncated,
                "");

            report(
                "用量远未贴顶不判为截断",
                TurnOutcome.Classify("stop", false, 50, 4000, 8192) == StepStall.None,
                "");

            // 已经发出工具调用时不算停顿：工具结果会自然带出下一步。
            report(
                "截断但已有工具调用时不算停顿",
                TurnOutcome.Classify("length", true, 0, 8192, 8192) == StepStall.None,
                "");

            // 空产出：没截断也没产出，收尾等于把未完成的任务丢给用户。
            report(
                "无正文无工具调用判为空产出",
                TurnOutcome.Classify("stop", false, 0, 40, 8192) == StepStall.Empty,
                "");

            // 服务端没报用量时不能凭 0 判断。
            report(
                "未报用量且有正文时正常收尾",
                TurnOutcome.Classify("stop", false, 200, 0, 8192) == StepStall.None,
                "");

            // 用量必须按步取，不能沿用会话累计值。上一步贴顶（截断的常态）
            // 而这一步没报用量时，传 0 才不会把这一步也判成截断。
            report(
                "未报用量且有工具调用时正常收尾",
                TurnOutcome.Classify(null, true, 0, 0, 8192) == StepStall.None,
                "");
        }

        private static void TestTruncatedJsonDetection(Action<string, bool, string> report)
        {
            // 被截断的参数：括号没配平。实测断在 values 数组中途。
            var cut = @"{""range"":""A2:B200"",""values"":[[""甲"",1],[""乙"",2";
            report("残缺参数判为截断", TurnOutcome.LooksTruncatedJson(cut), "");

            // 字符串字面量正中间断掉。
            report(
                "字符串未闭合判为截断",
                TurnOutcome.LooksTruncatedJson(@"{""range"":""A2:B2"",""values"":[[""未闭合"),
                "");

            // 完整但语义写错（值该是数组却给了对象）不该判成截断——
            // 这类要让模型改格式，而不是叫它分批。
            report(
                "完整 JSON 不判为截断",
                !TurnOutcome.LooksTruncatedJson(@"{""range"":""A1"",""values"":{""x"":1}}"),
                "");

            // 数据里带括号和转义引号，不能被计进配平。
            report(
                "数据含括号与转义引号时不误判",
                !TurnOutcome.LooksTruncatedJson(@"{""values"":[[""a{b}[c]"",""说\""话""]]}"),
                "");

            report("空参数不判为截断", !TurnOutcome.LooksTruncatedJson(""), "");

            // 给模型的说明必须点出「分批」，否则它会原样重发。
            var advice = TurnOutcome.DescribeTruncatedArguments("write_values", 51);
            report(
                "截断说明含分批指示",
                advice.Contains("拆成多次") && advice.Contains("51"),
                advice);
        }
    }
}
