using System;
using System.Linq;
using ChatSheet.AddIn.Agent;
using ChatSheet.AddIn.Providers;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 上下文管理验证。
    ///
    /// 压缩时机从「超限才压」改成「达到 90% 就压、压到 70%」，
    /// 这个改动的正确性无法靠端到端测试覆盖（正常对话很难堆到阈值），
    /// 因此在此对纯逻辑做直接验证。
    /// </summary>
    internal static class ContextTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestTokenEstimation(report);
            TestNoTrimBelowThreshold(report);
            TestTrimAtThreshold(report);
            TestSystemPromptPreserved(report);
            TestRecentMessagesPreserved(report);
            TestUsageTracking(report);
        }

        /// <summary>造一条指定大致 token 量的消息。</summary>
        private static ChatMessage ToolResult(string id, int approxTokens)
        {
            // 中文约 1.5 字/token，据此反推需要的字符数。
            var text = new string('数', Math.Max(1, (int)(approxTokens * 1.5)));
            return ChatMessage.FromToolResult(id, "read_range", text);
        }

        private static void TestTokenEstimation(Action<string, bool, string> report)
        {
            report("空文本估算为 0", Conversation.EstimateTokens("") == 0, "");
            report("null 估算为 0", Conversation.EstimateTokens(null) == 0, "");

            // 中文与英文的估算密度不同，中文每 token 覆盖的字符更少。
            var cjk = Conversation.EstimateTokens(new string('测', 150));
            var ascii = Conversation.EstimateTokens(new string('a', 150));
            report(
                "中文估算 token 多于同长英文",
                cjk > ascii,
                $"中文 {cjk}，英文 {ascii}");

            // 估算应偏保守（偏高），以免真正超限。
            report("150 个中文字约 100 token", cjk >= 90 && cjk <= 110, $"实际 {cjk}");
        }

        private static void TestNoTrimBelowThreshold(Action<string, bool, string> report)
        {
            var conversation = new Conversation();
            conversation.SetSystemPrompt("系统提示");
            for (var i = 0; i < 5; i++)
            {
                conversation.Add(ToolResult($"t{i}", 100));
            }

            var budget = 10_000;
            var before = conversation.EstimateTotalTokens();
            var result = conversation.TrimToBudget(budget);

            report(
                "未达阈值不压缩",
                !result.Trimmed && result.TokensAfter == before,
                $"before={before} after={result.TokensAfter} trimmed={result.Trimmed}");

            report(
                "阈值为预算的 90%",
                result.TriggerTokens == (int)(budget * 0.9),
                $"实际 {result.TriggerTokens}，期望 {(int)(budget * 0.9)}");

            report("消息数量未变", conversation.Messages.Count == 6, $"实际 {conversation.Messages.Count}");
        }

        private static void TestTrimAtThreshold(Action<string, bool, string> report)
        {
            var conversation = new Conversation();
            conversation.SetSystemPrompt("系统提示");

            // 堆到明显超过阈值。
            for (var i = 0; i < 30; i++)
            {
                conversation.Add(ToolResult($"t{i}", 200));
            }

            var budget = 3000;
            var before = conversation.EstimateTotalTokens();
            var result = conversation.TrimToBudget(budget);

            report(
                "超过阈值触发压缩",
                result.Trimmed,
                $"before={before} trigger={result.TriggerTokens} trimmed={result.Trimmed}");

            report(
                "压缩后小于压缩前",
                result.TokensAfter < result.TokensBefore,
                $"{result.TokensBefore} → {result.TokensAfter}");

            // 目标是 70%，留出后续轮次余量；压到 90% 就停会导致反复触发。
            report(
                "压缩后低于阈值",
                result.TokensAfter < result.TriggerTokens,
                $"after={result.TokensAfter} trigger={result.TriggerTokens}");

            report(
                "记录了压缩与丢弃数量",
                result.CompressedToolResults > 0 || result.DroppedMessages > 0,
                $"压缩 {result.CompressedToolResults}，丢弃 {result.DroppedMessages}");

            // 丢弃消息时必须留下说明，否则模型会以为上下文完整。
            if (result.DroppedMessages > 0)
            {
                var hasNotice = conversation.Messages.Any(m =>
                    m.Content != null && m.Content.Contains("已被移除"));
                report("丢弃后插入说明消息", hasNotice, "未找到说明");
            }
        }

        private static void TestSystemPromptPreserved(Action<string, bool, string> report)
        {
            var conversation = new Conversation();
            conversation.SetSystemPrompt("这是必须保留的系统提示");
            for (var i = 0; i < 40; i++)
            {
                conversation.Add(ToolResult($"t{i}", 300));
            }

            conversation.TrimToBudget(2000);

            var system = conversation.Messages.FirstOrDefault(m => m.Role == ChatRole.System);
            report(
                "系统提示始终保留",
                system != null && system.Content == "这是必须保留的系统提示",
                system == null ? "系统提示丢失" : "内容被改动");

            // 系统提示应始终在首位，否则部分协议会拒绝。
            report(
                "系统提示位于首位",
                conversation.Messages.Count > 0 && conversation.Messages[0].Role == ChatRole.System,
                $"首条角色={conversation.Messages.FirstOrDefault()?.Role}");
        }

        private static void TestRecentMessagesPreserved(Action<string, bool, string> report)
        {
            var conversation = new Conversation();
            conversation.SetSystemPrompt("系统提示");

            for (var i = 0; i < 30; i++)
            {
                conversation.Add(ToolResult($"old{i}", 250));
            }

            // 最近的消息带可识别标记。
            conversation.Add(ChatMessage.FromUser("最近的用户输入"));
            conversation.Add(ChatMessage.FromAssistant("最近的助手回复"));

            conversation.TrimToBudget(2000);

            var texts = conversation.Messages.Select(m => m.Content ?? string.Empty).ToList();
            report(
                "最近的用户输入未被压缩",
                texts.Any(t => t.Contains("最近的用户输入")),
                "已丢失");
            report(
                "最近的助手回复未被压缩",
                texts.Any(t => t.Contains("最近的助手回复")),
                "已丢失");
        }

        private static void TestUsageTracking(Action<string, bool, string> report)
        {
            var conversation = new Conversation();
            conversation.RecordUsage(100, 50);
            conversation.RecordUsage(200, 80);

            report(
                "累计用量正确",
                conversation.TotalPromptTokens == 300 && conversation.TotalCompletionTokens == 130,
                $"入 {conversation.TotalPromptTokens}，出 {conversation.TotalCompletionTokens}");

            report(
                "最近一轮用量正确",
                conversation.LastPromptTokens == 200 && conversation.LastCompletionTokens == 80,
                $"入 {conversation.LastPromptTokens}，出 {conversation.LastCompletionTokens}");

            // 0 值不应覆盖上一次的有效值：部分服务端只在最后一帧给用量。
            conversation.RecordUsage(0, 0);
            report(
                "零值不覆盖有效用量",
                conversation.LastPromptTokens == 200,
                $"实际 {conversation.LastPromptTokens}");

            conversation.Clear();
            report(
                "清空会话重置最近用量",
                conversation.LastPromptTokens == 0 && conversation.Messages.Count == 0,
                "");
        }
    }
}
