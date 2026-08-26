using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ChatSheet.AddIn.Providers;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 流式解析验证。用各协议真实格式的 SSE 样本驱动解析器。
    /// 工具调用的增量拼接尤其容易出错，必须在接上真实接口前先验证。
    /// </summary>
    internal static class StreamTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestSseFraming(report);
            TestOpenAiChat(report);
            TestOpenAiResponses(report);
            TestAnthropic(report);
            TestGemini(report);
            TestErrorExtraction(report);
            TestModelExtraction(report);
        }

        private static List<ChatEvent> Drive(ProtocolKind protocol, string sse)
        {
            var parser = StreamParser.Create(protocol);
            var events = new List<ChatEvent>();

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse)))
            {
                SseReader.ReadAsync(
                    stream,
                    frame =>
                    {
                        events.AddRange(parser.Parse(frame));
                        return System.Threading.Tasks.Task.FromResult(true);
                    },
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            events.AddRange(parser.Flush());
            return events;
        }

        private static string TextOf(IEnumerable<ChatEvent> events) =>
            string.Concat(events.Where(e => e.Kind == ChatEventKind.TextDelta).Select(e => e.Text));

        private static string ThinkingOf(IEnumerable<ChatEvent> events) =>
            string.Concat(events.Where(e => e.Kind == ChatEventKind.ThinkingDelta).Select(e => e.Text));

        private static List<ToolCall> CallsOf(IEnumerable<ChatEvent> events) =>
            events.Where(e => e.Kind == ChatEventKind.ToolCall).Select(e => e.Call).ToList();

        private static void TestSseFraming(Action<string, bool, string> report)
        {
            // 多行 data 需以换行拼接；注释行与 \r\n 都要正确处理。
            var sse = ": 心跳\r\n" +
                      "event: custom\r\n" +
                      "data: 第一行\r\n" +
                      "data: 第二行\r\n" +
                      "\r\n";

            var frames = new List<SseFrame>();
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse)))
            {
                SseReader.ReadAsync(
                    stream,
                    frame => { frames.Add(frame); return System.Threading.Tasks.Task.FromResult(true); },
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            report("SSE 分帧数量", frames.Count == 1, $"实际 {frames.Count}");
            if (frames.Count == 1)
            {
                report("SSE 事件名", frames[0].EventName == "custom", frames[0].EventName);
                report("SSE 多行拼接", frames[0].Data == "第一行\n第二行", frames[0].Data);
            }

            // 中文跨读取边界不能被切坏。这里用超过缓冲区的长中文串验证增量解码。
            var longChinese = new string('测', 5000);
            var sse2 = "data: " + longChinese + "\n\n";
            var frames2 = new List<SseFrame>();
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse2)))
            {
                SseReader.ReadAsync(
                    stream,
                    frame => { frames2.Add(frame); return System.Threading.Tasks.Task.FromResult(true); },
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            report(
                "SSE 中文跨边界不乱码",
                frames2.Count == 1 && frames2[0].Data == longChinese,
                frames2.Count == 1 ? $"长度 {frames2[0].Data.Length}，期望 {longChinese.Length}" : $"帧数 {frames2.Count}");
        }

        private static void TestOpenAiChat(Action<string, bool, string> report)
        {
            // 正文分多帧下发。
            var text = "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n" +
                       "data: {\"choices\":[{\"delta\":{\"content\":\"，世界\"}}]}\n\n" +
                       "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":5}}\n\n" +
                       "data: [DONE]\n\n";

            var events = Drive(ProtocolKind.OpenAiChatCompletions, text);
            report("OpenAI 正文拼接", TextOf(events) == "你好，世界", TextOf(events));
            report(
                "OpenAI 用量",
                events.Any(e => e.Kind == ChatEventKind.Usage && e.PromptTokens == 12 && e.CompletionTokens == 5),
                "");
            report("OpenAI 结束事件", events.Any(e => e.Kind == ChatEventKind.Completed), "");

            // 工具调用参数按字符增量下发，必须按 index 累积。
            var tools = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"write_values\",\"arguments\":\"\"}}]}}]}\n\n" +
                        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"range\\\":\"}}]}}]}\n\n" +
                        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"A1\\\"}\"}}]}}]}\n\n" +
                        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n";

            var toolEvents = Drive(ProtocolKind.OpenAiChatCompletions, tools);
            var calls = CallsOf(toolEvents);
            report("OpenAI 工具调用数量", calls.Count == 1, $"实际 {calls.Count}");
            if (calls.Count == 1)
            {
                report("OpenAI 工具名", calls[0].Name == "write_values", calls[0].Name);
                report("OpenAI 工具标识", calls[0].Id == "call_1", calls[0].Id);
                report(
                    "OpenAI 参数增量拼接",
                    calls[0].ArgumentsJson == "{\"range\":\"A1\"}",
                    calls[0].ArgumentsJson);
            }

            // 两个并行工具调用要按 index 分别累积，不能串台。
            var parallel = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                           "{\"index\":0,\"id\":\"c0\",\"function\":{\"name\":\"read_range\",\"arguments\":\"{\\\"range\\\":\\\"A1\\\"}\"}}," +
                           "{\"index\":1,\"id\":\"c1\",\"function\":{\"name\":\"get_selection\",\"arguments\":\"{}\"}}]}}]}\n\n" +
                           "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n";
            var parallelCalls = CallsOf(Drive(ProtocolKind.OpenAiChatCompletions, parallel));
            report(
                "OpenAI 并行工具调用",
                parallelCalls.Count == 2 &&
                parallelCalls.Any(c => c.Name == "read_range" && c.ArgumentsJson == "{\"range\":\"A1\"}") &&
                parallelCalls.Any(c => c.Name == "get_selection"),
                $"实际 {parallelCalls.Count} 个");

            // 兼容服务把思考放在 reasoning_content。
            var reasoning = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"先看结构\"}}]}\n\n" +
                            "data: {\"choices\":[{\"delta\":{\"content\":\"结论\"}}]}\n\n";
            var reasoningEvents = Drive(ProtocolKind.OpenAiChatCompletions, reasoning);
            report("OpenAI 思考内容分离", ThinkingOf(reasoningEvents) == "先看结构" && TextOf(reasoningEvents) == "结论", "");
        }

        private static void TestOpenAiResponses(Action<string, bool, string> report)
        {
            var sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"处理\"}\n\n" +
                      "data: {\"type\":\"response.output_text.delta\",\"delta\":\"完成\"}\n\n" +
                      "data: {\"type\":\"response.output_item.added\",\"item\":{\"id\":\"item_1\",\"type\":\"function_call\",\"call_id\":\"call_9\",\"name\":\"read_range\"}}\n\n" +
                      "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"item_1\",\"delta\":\"{\\\"range\\\":\"}\n\n" +
                      "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"item_1\",\"delta\":\"\\\"B2\\\"}\"}\n\n" +
                      "data: {\"type\":\"response.output_item.done\",\"item\":{\"id\":\"item_1\"}}\n\n" +
                      "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":30,\"output_tokens\":8}}}\n\n";

            var events = Drive(ProtocolKind.OpenAiResponses, sse);
            report("Responses 正文", TextOf(events) == "处理完成", TextOf(events));

            var calls = CallsOf(events);
            report("Responses 工具调用", calls.Count == 1, $"实际 {calls.Count}");
            if (calls.Count == 1)
            {
                report("Responses 工具标识用 call_id", calls[0].Id == "call_9", calls[0].Id);
                report("Responses 参数拼接", calls[0].ArgumentsJson == "{\"range\":\"B2\"}", calls[0].ArgumentsJson);
            }

            report(
                "Responses 用量",
                events.Any(e => e.Kind == ChatEventKind.Usage && e.PromptTokens == 30 && e.CompletionTokens == 8),
                "");
        }

        private static void TestAnthropic(Action<string, bool, string> report)
        {
            var sse = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":25}}}\n\n" +
                      "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\"}}\n\n" +
                      "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"分析中\"}}\n\n" +
                      "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
                      "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\"}}\n\n" +
                      "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"好的\"}}\n\n" +
                      "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
                      "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"write_values\"}}\n\n" +
                      "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"range\\\"\"}}\n\n" +
                      "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\":\\\"C3\\\"}\"}}\n\n" +
                      "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":2}\n\n" +
                      "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":40}}\n\n" +
                      "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

            var events = Drive(ProtocolKind.AnthropicMessages, sse);
            report("Anthropic 正文", TextOf(events) == "好的", TextOf(events));
            report("Anthropic 思考分离", ThinkingOf(events) == "分析中", ThinkingOf(events));

            var calls = CallsOf(events);
            report("Anthropic 工具调用", calls.Count == 1, $"实际 {calls.Count}");
            if (calls.Count == 1)
            {
                report("Anthropic 工具标识", calls[0].Id == "toolu_1", calls[0].Id);
                report("Anthropic 参数拼接", calls[0].ArgumentsJson == "{\"range\":\"C3\"}", calls[0].ArgumentsJson);
            }

            // 输入 token 来自 message_start，输出来自 message_delta，需合并上报。
            report(
                "Anthropic 用量合并",
                events.Any(e => e.Kind == ChatEventKind.Usage && e.PromptTokens == 25 && e.CompletionTokens == 40),
                "");

            TestAnthropicStopReason(report);
        }

        /// <summary>
        /// 结束原因必须从 message_delta 带到 message_stop。
        ///
        /// 两个事件是分开的：stop_reason 只在 message_delta 里，结束事件在
        /// message_stop。不接力的话上层永远收到空的结束原因，
        /// 也就分不出「说完了」和「被 max_tokens 截断」。
        /// </summary>
        private static void TestAnthropicStopReason(Action<string, bool, string> report)
        {
            var truncated =
                "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n\n" +
                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"话没说完\"}}\n\n" +
                "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"},\"usage\":{\"output_tokens\":8192}}\n\n" +
                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

            var events = Drive(ProtocolKind.AnthropicMessages, truncated);
            var completed = events.FirstOrDefault(e => e.Kind == ChatEventKind.Completed);

            report(
                "Anthropic 截断结束原因上报",
                completed != null && completed.FinishReason == "max_tokens",
                completed?.FinishReason ?? "<无结束事件>");

            var normal =
                "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":30}}\n\n" +
                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

            var normalCompleted = Drive(ProtocolKind.AnthropicMessages, normal)
                .FirstOrDefault(e => e.Kind == ChatEventKind.Completed);

            report(
                "Anthropic 正常结束原因上报",
                normalCompleted != null && normalCompleted.FinishReason == "end_turn",
                normalCompleted?.FinishReason ?? "<无结束事件>");
        }

        private static void TestGemini(Action<string, bool, string> report)
        {
            var sse = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"思考片段\",\"thought\":true},{\"text\":\"正文\"}]}}]}\n\n" +
                      "data: {\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"read_range\",\"args\":{\"range\":\"D4\"}}}]},\"finishReason\":\"STOP\"}]," +
                      "\"usageMetadata\":{\"promptTokenCount\":18,\"candidatesTokenCount\":6}}\n\n";

            var events = Drive(ProtocolKind.GoogleGemini, sse);
            report("Gemini 正文", TextOf(events) == "正文", TextOf(events));
            report("Gemini 思考分离", ThinkingOf(events) == "思考片段", ThinkingOf(events));

            var calls = CallsOf(events);
            report("Gemini 工具调用", calls.Count == 1, $"实际 {calls.Count}");
            if (calls.Count == 1)
            {
                report("Gemini 工具名", calls[0].Name == "read_range", calls[0].Name);
                report(
                    "Gemini 参数为完整对象",
                    calls[0].ArgumentsJson.Contains("\"range\"") && calls[0].ArgumentsJson.Contains("D4"),
                    calls[0].ArgumentsJson);
                report("Gemini 自造调用标识", !string.IsNullOrEmpty(calls[0].Id), calls[0].Id);
            }

            report(
                "Gemini 用量",
                events.Any(e => e.Kind == ChatEventKind.Usage && e.PromptTokens == 18 && e.CompletionTokens == 6),
                "");
        }

        private static void TestErrorExtraction(Action<string, bool, string> report)
        {
            report(
                "错误提取 OpenAI 结构",
                ChatClient.ExtractErrorMessage("{\"error\":{\"message\":\"密钥无效\",\"type\":\"auth\"}}") == "密钥无效",
                "");

            report(
                "错误提取顶层 message",
                ChatClient.ExtractErrorMessage("{\"message\":\"额度不足\"}") == "额度不足",
                "");

            report(
                "错误提取字符串 error",
                ChatClient.ExtractErrorMessage("{\"error\":\"模型不存在\"}") == "模型不存在",
                "");

            // 网关返回 HTML 时不能崩，应原样截断展示。
            var html = ChatClient.ExtractErrorMessage("<html><body>502 Bad Gateway</body></html>");
            report("错误提取非 JSON 不崩", html.Contains("502"), html);
        }

        private static void TestModelExtraction(Action<string, bool, string> report)
        {
            var openai = ChatClient.ExtractModelIds("{\"data\":[{\"id\":\"gpt-4o\"},{\"id\":\"gpt-4o-mini\"}]}");
            report("模型提取 OpenAI", openai.Count == 2 && openai.Contains("gpt-4o"), string.Join(",", openai));

            // Gemini 的 models/ 前缀应被去掉。
            var gemini = ChatClient.ExtractModelIds("{\"models\":[{\"name\":\"models/gemini-2.0-flash\"}]}");
            report("模型提取 Gemini 去前缀", gemini.Count == 1 && gemini[0] == "gemini-2.0-flash", string.Join(",", gemini));

            var anthropic = ChatClient.ExtractModelIds("{\"data\":[{\"id\":\"claude-sonnet-4\",\"display_name\":\"Sonnet\"}]}");
            report("模型提取 Anthropic", anthropic.Count == 1 && anthropic[0] == "claude-sonnet-4", string.Join(",", anthropic));

            // 去重与排序。
            var dup = ChatClient.ExtractModelIds("{\"data\":[{\"id\":\"b\"},{\"id\":\"a\"},{\"id\":\"b\"}]}");
            report("模型去重排序", dup.Count == 2 && dup[0] == "a", string.Join(",", dup));

            report("模型提取空响应不崩", ChatClient.ExtractModelIds("{}").Count == 0, "");
            report("模型提取非 JSON 不崩", ChatClient.ExtractModelIds("not json").Count == 0, "");
        }
    }
}
