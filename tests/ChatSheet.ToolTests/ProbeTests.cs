using System;
using System.Linq;
using ChatSheet.AddIn.Providers;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 按需确认的请求形态验证。
    ///
    /// 这里的核心不变量是「探测请求是真实请求的真子集」：只准去掉字段，不准换值。
    /// 换了值就有两种误判，其中「探测绿灯而真实对话失败」是主动骗人。
    /// 因此每条断言都在盯同一件事——请求体里那个字段到底在不在。
    /// </summary>
    internal static class ProbeTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestSuppressThinking(report);
            TestMessagesNeverEmpty(report);
            TestOutputLimitField(report);
            TestOutputLimitSignal(report);
            TestRetryBudget(report);
        }

        private static ChatRequest Base(ProtocolKind protocol, bool suppress)
        {
            var request = new ChatRequest
            {
                Protocol = protocol,
                BaseUrl = "https://gw.example.test/v1",
                Token = "t",
                Model = "some-model",
                // 刻意用 High：真实对话的默认档就是 High，而探测要证明自己不带它。
                Thinking = ThinkingLevel.High,
                MaxOutputTokens = 16,
                IncludeTools = false,
                SuppressThinking = suppress,
            };

            request.Messages.Add(ChatMessage.FromUser("hi"));
            return request;
        }

        /// <summary>请求体里出现过的所有键名，含嵌套。</summary>
        private static string AllKeys(JObject body)
        {
            var keys = body.Descendants()
                .OfType<JProperty>()
                .Select(p => p.Name);
            return string.Join(",", keys);
        }

        private static void TestSuppressThinking(Action<string, bool, string> report)
        {
            var protocols = new[]
            {
                ProtocolKind.OpenAiChatCompletions,
                ProtocolKind.OpenAiResponses,
                ProtocolKind.AnthropicMessages,
                ProtocolKind.GoogleGemini,
            };

            // 思考相关的键名，四个协议各自的写法都在内。
            var thinkingKeys = new[]
            {
                "reasoning_effort", "reasoning", "thinking",
                "output_config", "thinkingConfig", "thinkingLevel",
                "thinkingBudget", "budget_tokens",
            };

            foreach (var protocol in protocols)
            {
                var id = Protocols.Get(protocol).Id;

                var suppressed = RequestBuilder.Build(Base(protocol, suppress: true), stream: true);
                var keys = AllKeys(suppressed);
                var leaked = thinkingKeys.Where(k => keys.Split(',').Contains(k)).ToList();

                report(
                    $"{id}：SuppressThinking 时请求体不含任何思考键",
                    leaked.Count == 0,
                    leaked.Count == 0 ? "" : "泄漏：" + string.Join("、", leaked));

                // 不设时必须与今天逐字一致——否则这个开关会顺带改了真实对话。
                var normal = RequestBuilder.Build(Base(protocol, suppress: false), stream: true);
                var normalKeys = AllKeys(normal).Split(',');
                report(
                    $"{id}：不设 SuppressThinking 时仍带思考参数",
                    thinkingKeys.Any(k => normalKeys.Contains(k)),
                    AllKeys(normal));
            }

            // 关键反例：Off 不等于「不传」。Off 会发出一个值，而那个值会被某些网关拒绝。
            var off = Base(ProtocolKind.OpenAiChatCompletions, suppress: false);
            off.Thinking = ThinkingLevel.Off;
            var offBody = RequestBuilder.Build(off, stream: true);
            report(
                "Thinking=Off 仍然会写 reasoning_effort（所以 Off 不能当作「不传」）",
                offBody["reasoning_effort"] != null &&
                    offBody.Value<string>("reasoning_effort") == "none",
                offBody.ToString(Newtonsoft.Json.Formatting.None));

            var geminiOff = Base(ProtocolKind.GoogleGemini, suppress: false);
            geminiOff.Thinking = ThinkingLevel.Off;
            var geminiBody = RequestBuilder.Build(geminiOff, stream: true);
            report(
                "Gemini 的 Off 会写 thinkingBudget=0（同样是一个值）",
                AllKeys(geminiBody).Split(',').Contains("thinkingBudget"),
                geminiBody.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>
        /// Anthropic 与 Gemini 会把 system 抽到顶层，只带系统提示会产出空消息列表。
        /// 探测必须带一条 user 消息，否则这两个协议上恒定 400。
        /// </summary>
        private static void TestMessagesNeverEmpty(Action<string, bool, string> report)
        {
            var anthropic = RequestBuilder.Build(
                Base(ProtocolKind.AnthropicMessages, suppress: true), stream: true);
            report(
                "Anthropic 探测请求的 messages 非空",
                anthropic["messages"] is JArray am && am.Count > 0,
                anthropic.ToString(Newtonsoft.Json.Formatting.None));

            var gemini = RequestBuilder.Build(
                Base(ProtocolKind.GoogleGemini, suppress: true), stream: true);
            report(
                "Gemini 探测请求的 contents 非空",
                gemini["contents"] is JArray gc && gc.Count > 0,
                gemini.ToString(Newtonsoft.Json.Formatting.None));

            // 反证这条断言不是白测的：只带 system 时确实会空。
            var systemOnly = new ChatRequest
            {
                Protocol = ProtocolKind.AnthropicMessages,
                BaseUrl = "https://gw.example.test/v1",
                Model = "m",
                SuppressThinking = true,
            };
            systemOnly.Messages.Add(new ChatMessage { Role = ChatRole.System, Content = "sys" });
            var empty = RequestBuilder.Build(systemOnly, stream: true);
            report(
                "只带 system 时 Anthropic 的 messages 真的会空（所以上一条不是白测）",
                !(empty["messages"] is JArray ea) || ea.Count == 0,
                empty.ToString(Newtonsoft.Json.Formatting.None));

            // 探测不带工具。
            var probe = RequestBuilder.Build(
                Base(ProtocolKind.OpenAiChatCompletions, suppress: true), stream: true);
            report(
                "探测请求不带工具声明",
                probe["tools"] == null && probe["tool_choice"] == null,
                AllKeys(probe));
        }

        private static void TestOutputLimitField(Action<string, bool, string> report)
        {
            var def = Base(ProtocolKind.OpenAiChatCompletions, suppress: true);
            var defBody = RequestBuilder.Build(def, stream: true);
            report(
                "未被拒过时用 max_tokens",
                defBody["max_tokens"] != null && defBody["max_completion_tokens"] == null,
                AllKeys(defBody));

            var overridden = Base(ProtocolKind.OpenAiChatCompletions, suppress: true);
            overridden.OutputLimitOverride = OutputLimitField.MaxCompletionTokens;
            var overriddenBody = RequestBuilder.Build(overridden, stream: true);
            report(
                "被拒过后改用 max_completion_tokens 且不再发 max_tokens",
                overriddenBody["max_completion_tokens"] != null && overriddenBody["max_tokens"] == null,
                AllKeys(overriddenBody));

            // 字段名的选择不许依赖模型名——这条锁住「不按名字猜」。
            var reasoningName = Base(ProtocolKind.OpenAiChatCompletions, suppress: true);
            reasoningName.Model = "o3-mini";
            var byName = RequestBuilder.Build(reasoningName, stream: true);
            report(
                "模型名像推理模型也不改变字段名（不按名字猜）",
                byName["max_tokens"] != null && byName["max_completion_tokens"] == null,
                AllKeys(byName));

            // 档案是按「连接 + 模型」记的，不牵连别的模型。
            ModelCapabilities.Reset();
            const string conn = "CustomApi|openai|https://gw.example.test/v1";
            ModelCapabilities.For(conn, "o3-mini").OutputLimit = OutputLimitField.MaxCompletionTokens;
            report(
                "输出上限档案按模型隔离",
                ModelCapabilities.For(conn, "o3-mini").OutputLimit == OutputLimitField.MaxCompletionTokens &&
                    ModelCapabilities.For(conn, "gpt-4o").OutputLimit == null,
                "");
            ModelCapabilities.Reset();
        }

        private static ProviderException Http(int status, string detail, string message = null)
        {
            return new ProviderException("HTTP_" + status, message ?? detail) { Detail = detail };
        }

        private static void TestOutputLimitSignal(Action<string, bool, string> report)
        {
            report(
                "「请改用 max_completion_tokens」判为字段名错",
                CapabilitySignals.LooksLikeOutputLimitFieldWrong(Http(400,
                    "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.")),
                "");

            report(
                "「unknown parameter: max_tokens」判为字段名错",
                CapabilitySignals.LooksLikeOutputLimitFieldWrong(Http(400,
                    "unknown parameter: max_tokens")),
                "");

            // 反例：只回显了请求体，没说这个字段不对。
            report(
                "只回显含 max_tokens 的请求体不判为字段名错",
                !CapabilitySignals.LooksLikeOutputLimitFieldWrong(Http(400,
                    "invalid request body: {\"model\":\"m\",\"max_tokens\":16}")),
                "网关把请求体回显在错误里时，每条错误都会含这个字段名");

            // 反例：5xx 与账号问题都不是字段名的事。
            report(
                "503 不判为字段名错",
                !CapabilitySignals.LooksLikeOutputLimitFieldWrong(Http(503, "max_tokens upstream unavailable")),
                "");

            report(
                "401 不判为字段名错",
                !CapabilitySignals.LooksLikeOutputLimitFieldWrong(Http(401, "invalid api key")),
                "");

            // 交叉反例：这条判据不该和工具/视觉判据抢。
            report(
                "字段名错不判为缺工具",
                !CapabilitySignals.LooksLikeToolUnsupported(Http(400,
                    "Unsupported parameter: 'max_tokens'. Use 'max_completion_tokens' instead.")),
                "");

            report(
                "缺工具不判为字段名错",
                !CapabilitySignals.LooksLikeOutputLimitFieldWrong(Http(400, "unknown parameter: tool_choice")),
                "");
        }

        /// <summary>
        /// 探测不走完整退避。这里只断言预算本身，真实的等待时长由 RetryPolicy 决定。
        /// </summary>
        private static void TestRetryBudget(Action<string, bool, string> report)
        {
            report(
                "完整退避确实是二十几秒（所以探测必须传 maxRetries: 0）",
                RetryPolicy.TotalBackoff.TotalSeconds >= 20,
                RetryPolicy.TotalBackoff.ToString());

            report(
                "我方超时的错误码与用户取消可区分",
                !string.IsNullOrEmpty(ModelProbe.TimeoutCode),
                ModelProbe.TimeoutCode);

            // 我方超时必须判未知：它说的是网关没答话，不是模型不存在。
            report(
                "我方超时判未知",
                ModelAvailability.Classify(
                    new ProviderException(ModelProbe.TimeoutCode, "确认超时"), "m")
                    == AvailabilityVerdict.Unknown,
                "");
        }
    }
}
