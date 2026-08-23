using System;
using System.Linq;
using ChatSheet.AddIn.Providers;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 思考参数映射验证。
    ///
    /// 这部分按官方文档重写过，风险集中在 Anthropic：
    /// thinking.type=enabled + budget_tokens 在 4.7 及更新的模型上返回 400，
    /// 必须改用 thinking.type=adaptive + output_config.effort。
    /// 模型代际判断错会导致整轮对话失败，因此逐个模型名验证。
    /// </summary>
    internal static class ThinkingTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestOpenAiMapping(report);
            TestAnthropicStyleDetection(report);
            TestAnthropicAdaptiveBody(report);
            TestAnthropicBudgetBody(report);
            TestGeminiMapping(report);
            TestSupportedLevels(report);
        }

        private static ChatRequest Request(ProtocolKind protocol, string model, ThinkingLevel level)
        {
            var request = new ChatRequest
            {
                Protocol = protocol,
                BaseUrl = "https://example.com/v1",
                Token = "t",
                Model = model,
                Thinking = level,
                MaxOutputTokens = 8192,
                IncludeTools = false,
            };
            request.Messages.Add(ChatMessage.FromUser("测试"));
            return request;
        }

        private static void TestOpenAiMapping(Action<string, bool, string> report)
        {
            // 官方取值：none / minimal / low / medium / high / xhigh / max
            var cases = new[]
            {
                new { Level = ThinkingLevel.Off, Expect = "none" },
                new { Level = ThinkingLevel.Minimal, Expect = "minimal" },
                new { Level = ThinkingLevel.Low, Expect = "low" },
                new { Level = ThinkingLevel.Medium, Expect = "medium" },
                new { Level = ThinkingLevel.High, Expect = "high" },
                new { Level = ThinkingLevel.XHigh, Expect = "xhigh" },
                new { Level = ThinkingLevel.Max, Expect = "max" },
            };

            foreach (var c in cases)
            {
                var body = RequestBuilder.Build(Request(ProtocolKind.OpenAiChatCompletions, "gpt-5.5", c.Level), stream: true);
                var actual = body.Value<string>("reasoning_effort");
                report($"OpenAI {c.Level} → {c.Expect}", actual == c.Expect, $"实际 {actual}");
            }

            // Responses 协议用嵌套的 reasoning.effort
            var responses = RequestBuilder.Build(
                Request(ProtocolKind.OpenAiResponses, "gpt-5.5", ThinkingLevel.XHigh), stream: true);
            var nested = (responses["reasoning"] as JObject)?.Value<string>("effort");
            report("Responses 用 reasoning.effort", nested == "xhigh", $"实际 {nested}");
        }

        private static void TestAnthropicStyleDetection(Action<string, bool, string> report)
        {
            // 4.7 及更新只接受 adaptive；4.5 及更早只接受 budget。
            var adaptive = new[]
            {
                "claude-opus-5", "claude-sonnet-5", "claude-fable-5", "claude-mythos-5",
                "claude-opus-4-7", "claude-opus-4-8",
                "claude-sonnet-4-6", "claude-opus-4-6",
                "claude-opus-4-5-20251101",
            };

            foreach (var model in adaptive)
            {
                var style = Thinking.StyleFor(model);
                report(
                    $"{model} 应用 adaptive",
                    style == AnthropicThinkingStyle.Adaptive,
                    $"实际 {style}");
            }

            var budget = new[] { "claude-sonnet-4-5", "claude-haiku-4-5", "claude-3-7-sonnet" };
            foreach (var model in budget)
            {
                var style = Thinking.StyleFor(model);
                report(
                    $"{model} 应用 budget",
                    style == AnthropicThinkingStyle.Budget,
                    $"实际 {style}");
            }

            // 模型名未知时偏向新方式：新模型会拒绝旧参数，旧模型只是忽略 effort。
            report(
                "未知模型名偏向 adaptive",
                Thinking.StyleFor(null) == AnthropicThinkingStyle.Adaptive &&
                Thinking.StyleFor("some-proxy-model") == AnthropicThinkingStyle.Adaptive,
                "");
        }

        private static void TestAnthropicAdaptiveBody(Action<string, bool, string> report)
        {
            // 用户实际使用的模型，必须走 adaptive，绝不能出现 budget_tokens。
            var body = RequestBuilder.Build(
                Request(ProtocolKind.AnthropicMessages, "claude-opus-5", ThinkingLevel.High), stream: true);

            var thinking = body["thinking"] as JObject;
            report("Opus5 thinking.type=adaptive", thinking?.Value<string>("type") == "adaptive", thinking?.ToString());
            report(
                "Opus5 不含 budget_tokens（否则返回 400）",
                thinking?["budget_tokens"] == null,
                thinking?.ToString());

            var effort = (body["output_config"] as JObject)?.Value<string>("effort");
            report("Opus5 effort=high", effort == "high", $"实际 {effort}");

            // minimal 在 Anthropic 无对应档，应就近降为 low 而非报错或丢弃。
            var minimal = RequestBuilder.Build(
                Request(ProtocolKind.AnthropicMessages, "claude-opus-5", ThinkingLevel.Minimal), stream: true);
            var minimalEffort = (minimal["output_config"] as JObject)?.Value<string>("effort");
            report("Anthropic minimal 降级为 low", minimalEffort == "low", $"实际 {minimalEffort}");

            // 关闭思考用 thinking.type=disabled 表达，不通过 effort。
            var off = RequestBuilder.Build(
                Request(ProtocolKind.AnthropicMessages, "claude-opus-5", ThinkingLevel.Off), stream: true);
            var offType = (off["thinking"] as JObject)?.Value<string>("type");
            report("Anthropic 关闭思考用 disabled", offType == "disabled", $"实际 {offType}");
            report("关闭思考时不传 effort", off["output_config"] == null, off["output_config"]?.ToString());

            // xhigh 与 max 应原样传递。
            foreach (var pair in new[]
            {
                new { Level = ThinkingLevel.XHigh, Expect = "xhigh" },
                new { Level = ThinkingLevel.Max, Expect = "max" },
            })
            {
                var b = RequestBuilder.Build(
                    Request(ProtocolKind.AnthropicMessages, "claude-opus-5", pair.Level), stream: true);
                var e = (b["output_config"] as JObject)?.Value<string>("effort");
                report($"Anthropic {pair.Level} → {pair.Expect}", e == pair.Expect, $"实际 {e}");
            }
        }

        private static void TestAnthropicBudgetBody(Action<string, bool, string> report)
        {
            var body = RequestBuilder.Build(
                Request(ProtocolKind.AnthropicMessages, "claude-sonnet-4-5", ThinkingLevel.Medium), stream: true);

            var thinking = body["thinking"] as JObject;
            report("Sonnet4.5 thinking.type=enabled", thinking?.Value<string>("type") == "enabled", thinking?.ToString());

            var budget = thinking?.Value<int?>("budget_tokens") ?? 0;
            report("Sonnet4.5 含 budget_tokens", budget > 0, $"实际 {budget}");
            // 官方硬性要求：下限 1024，且必须小于 max_tokens。
            report("budget_tokens ≥ 1024", budget >= 1024, $"实际 {budget}");
            report("budget_tokens < max_tokens", budget < 8192, $"实际 {budget} vs 8192");

            // 旧方式启用思考时 temperature 必须为 1，显式传值会被拒绝。
            var withTemp = Request(ProtocolKind.AnthropicMessages, "claude-sonnet-4-5", ThinkingLevel.Medium);
            withTemp.Temperature = 0.3;
            var tempBody = RequestBuilder.Build(withTemp, stream: true);
            report("旧方式启用思考时移除 temperature", tempBody["temperature"] == null, tempBody["temperature"]?.ToString());

            // 极小的 max_tokens 下也要满足下限要求。
            var tiny = Request(ProtocolKind.AnthropicMessages, "claude-sonnet-4-5", ThinkingLevel.Max);
            tiny.MaxOutputTokens = 1200;
            var tinyBody = RequestBuilder.Build(tiny, stream: true);
            var tinyBudget = (tinyBody["thinking"] as JObject)?.Value<int?>("budget_tokens") ?? 0;
            report("max_tokens 很小时 budget 仍 ≥ 1024", tinyBudget >= 1024, $"实际 {tinyBudget}");
        }

        private static void TestGeminiMapping(Action<string, bool, string> report)
        {
            // Gemini 用 thinkingLevel，只有 minimal/low/medium/high 四档。
            var high = RequestBuilder.Build(
                Request(ProtocolKind.GoogleGemini, "gemini-3.5-flash", ThinkingLevel.High), stream: true);
            var config = (high["generationConfig"] as JObject)?["thinkingConfig"] as JObject;
            report("Gemini 用 thinkingLevel", config?.Value<string>("thinkingLevel") == "high", config?.ToString());
            report("Gemini 请求思考摘要", config?.Value<bool?>("includeThoughts") == true, config?.ToString());

            // 超出 high 的档位应就近取 high，而非传入无效值。
            foreach (var level in new[] { ThinkingLevel.XHigh, ThinkingLevel.Max })
            {
                var b = RequestBuilder.Build(
                    Request(ProtocolKind.GoogleGemini, "gemini-3.5-flash", level), stream: true);
                var c = (b["generationConfig"] as JObject)?["thinkingConfig"] as JObject;
                report($"Gemini {level} 降级为 high", c?.Value<string>("thinkingLevel") == "high", c?.ToString());
            }

            // 关闭思考用 thinkingBudget=0。
            var off = RequestBuilder.Build(
                Request(ProtocolKind.GoogleGemini, "gemini-3.5-flash", ThinkingLevel.Off), stream: true);
            var offConfig = (off["generationConfig"] as JObject)?["thinkingConfig"] as JObject;
            report("Gemini 关闭思考用 thinkingBudget=0", offConfig?.Value<int?>("thinkingBudget") == 0, offConfig?.ToString());
        }

        private static void TestSupportedLevels(Action<string, bool, string> report)
        {
            var anthropic = Thinking.SupportedLevels(ProtocolKind.AnthropicMessages);
            report(
                "Anthropic 支持档位不含 Minimal",
                !anthropic.Contains("Minimal") && anthropic.Contains("Max"),
                string.Join(",", anthropic));

            var gemini = Thinking.SupportedLevels(ProtocolKind.GoogleGemini);
            report(
                "Gemini 支持档位不含 XHigh/Max",
                !gemini.Contains("XHigh") && !gemini.Contains("Max") && gemini.Contains("Minimal"),
                string.Join(",", gemini));

            var openai = Thinking.SupportedLevels(ProtocolKind.OpenAiChatCompletions);
            report("OpenAI 支持全部七档", openai.Count == 7, string.Join(",", openai));

            // 界面用的选项清单必须与枚举一一对应，漏项会导致某档位无法选择。
            report(
                "选项清单覆盖全部档位",
                Thinking.Options.Count == Enum.GetValues(typeof(ThinkingLevel)).Length,
                $"选项 {Thinking.Options.Count} 个，枚举 {Enum.GetValues(typeof(ThinkingLevel)).Length} 个");
        }
    }
}
