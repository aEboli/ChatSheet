using System;
using System.Collections.Generic;
using ChatSheet.AddIn.Tools;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 按协议构造请求体。四种协议的消息结构、工具声明与思考参数各不相同，
    /// 差异集中在这里，其余代码只面对统一的 ChatRequest。
    /// </summary>
    internal static class RequestBuilder
    {
        internal static JObject Build(ChatRequest request, bool stream)
        {
            switch (request.Protocol)
            {
                case ProtocolKind.AnthropicMessages:
                    return BuildAnthropic(request, stream);
                case ProtocolKind.GoogleGemini:
                    return BuildGemini(request);
                case ProtocolKind.OpenAiResponses:
                    return BuildOpenAiResponses(request, stream);
                default:
                    return BuildOpenAiChat(request, stream);
            }
        }

        // ---- OpenAI Chat Completions ----

        private static JObject BuildOpenAiChat(ChatRequest request, bool stream)
        {
            var messages = new JArray();
            foreach (var message in request.Messages)
            {
                switch (message.Role)
                {
                    case ChatRole.Tool:
                        messages.Add(new JObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = message.ToolCallId,
                            ["content"] = message.Content ?? string.Empty,
                        });
                        break;

                    case ChatRole.Assistant when message.ToolCalls.Count > 0:
                        var calls = new JArray();
                        foreach (var call in message.ToolCalls)
                        {
                            calls.Add(new JObject
                            {
                                ["id"] = call.Id,
                                ["type"] = "function",
                                ["function"] = new JObject
                                {
                                    ["name"] = call.Name,
                                    ["arguments"] = call.ArgumentsJson ?? "{}",
                                },
                            });
                        }

                        messages.Add(new JObject
                        {
                            ["role"] = "assistant",
                            // 有工具调用时 content 可为空，但字段必须存在。
                            ["content"] = message.Content ?? string.Empty,
                            ["tool_calls"] = calls,
                        });
                        break;

                    default:
                        messages.Add(new JObject
                        {
                            ["role"] = RoleName(message.Role),
                            // 带图片时 content 必须是数组形式。
                            ["content"] = message.Images.Count > 0
                                ? (JToken)BuildOpenAiChatContent(message)
                                : message.Content ?? string.Empty,
                        });
                        break;
                }
            }

            var body = new JObject
            {
                ["model"] = request.Model,
                ["messages"] = messages,
                ["stream"] = stream,
            };

            if (stream)
            {
                // 请求用量统计：默认流式响应不含 usage。
                body["stream_options"] = new JObject { ["include_usage"] = true };
            }

            if (request.Temperature.HasValue) { body["temperature"] = request.Temperature.Value; }
            if (request.MaxOutputTokens.HasValue) { body["max_tokens"] = request.MaxOutputTokens.Value; }

            if (request.IncludeTools)
            {
                body["tools"] = OpenAiTools();
                body["tool_choice"] = "auto";
            }

            // OpenAI 系用 reasoning_effort 表达思考强度，
            // 取值 none/minimal/low/medium/high/xhigh/max，各模型支持其中的子集。
            var effort = Thinking.OpenAiEffort(request.Thinking);
            if (effort != null)
            {
                body["reasoning_effort"] = effort;
            }

            return body;
        }

        /// <summary>
        /// Chat Completions 的多模态 content 数组。
        /// 图片用 image_url，其 url 字段接受 data URL 形式的 base64。
        /// </summary>
        private static JArray BuildOpenAiChatContent(ChatMessage message)
        {
            var content = new JArray();

            // 图片放在文本之前：多数视觉模型对「先图后问」的效果更好。
            foreach (var image in message.Images)
            {
                content.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject { ["url"] = image.ToDataUrl() },
                });
            }

            if (!string.IsNullOrEmpty(message.Content))
            {
                content.Add(new JObject { ["type"] = "text", ["text"] = message.Content });
            }

            return content;
        }

        private static JArray OpenAiTools()
        {
            var tools = new JArray();
            foreach (var tool in ToolCatalog.All)
            {
                tools.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JObject.FromObject(tool.Parameters),
                    },
                });
            }

            return tools;
        }

        // ---- OpenAI Responses ----

        private static JObject BuildOpenAiResponses(ChatRequest request, bool stream)
        {
            var input = new JArray();
            foreach (var message in request.Messages)
            {
                if (message.Role == ChatRole.Tool)
                {
                    input.Add(new JObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = message.ToolCallId,
                        ["output"] = message.Content ?? string.Empty,
                    });
                    continue;
                }

                if (message.Role == ChatRole.Assistant && message.ToolCalls.Count > 0)
                {
                    foreach (var call in message.ToolCalls)
                    {
                        input.Add(new JObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call.Id,
                            ["name"] = call.Name,
                            ["arguments"] = call.ArgumentsJson ?? "{}",
                        });
                    }

                    continue;
                }

                input.Add(new JObject
                {
                    ["role"] = RoleName(message.Role),
                    // Responses 协议的图片类型是 input_image，与 Chat Completions 不同。
                    ["content"] = message.Images.Count > 0
                        ? (JToken)BuildResponsesContent(message)
                        : message.Content ?? string.Empty,
                });
            }

            var body = new JObject
            {
                ["model"] = request.Model,
                ["input"] = input,
                ["stream"] = stream,
            };

            if (request.Temperature.HasValue) { body["temperature"] = request.Temperature.Value; }
            if (request.MaxOutputTokens.HasValue) { body["max_output_tokens"] = request.MaxOutputTokens.Value; }

            if (request.IncludeTools)
            {
                var tools = new JArray();
                foreach (var tool in ToolCatalog.All)
                {
                    // Responses 协议的工具是平铺结构，不像 Chat Completions 那样嵌 function。
                    tools.Add(new JObject
                    {
                        ["type"] = "function",
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JObject.FromObject(tool.Parameters),
                    });
                }

                body["tools"] = tools;
            }

            var effort = Thinking.OpenAiEffort(request.Thinking);
            if (effort != null)
            {
                body["reasoning"] = new JObject { ["effort"] = effort };
            }

            return body;
        }

        /// <summary>
        /// Responses 协议的多模态 content。
        /// 类型名与 Chat Completions 不同：input_text / input_image，
        /// 且 image_url 直接是字符串而非对象。
        /// </summary>
        private static JArray BuildResponsesContent(ChatMessage message)
        {
            var content = new JArray();

            foreach (var image in message.Images)
            {
                content.Add(new JObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = image.ToDataUrl(),
                });
            }

            if (!string.IsNullOrEmpty(message.Content))
            {
                content.Add(new JObject { ["type"] = "input_text", ["text"] = message.Content });
            }

            return content;
        }

        // ---- Anthropic Messages ----

        private static JObject BuildAnthropic(ChatRequest request, bool stream)
        {
            var messages = new JArray();
            string systemPrompt = null;

            foreach (var message in request.Messages)
            {
                if (message.Role == ChatRole.System)
                {
                    // Anthropic 的系统提示是顶层字段，不在 messages 里。
                    systemPrompt = string.IsNullOrEmpty(systemPrompt)
                        ? message.Content
                        : systemPrompt + "\n\n" + message.Content;
                    continue;
                }

                if (message.Role == ChatRole.Tool)
                {
                    messages.Add(new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = message.ToolCallId,
                                ["content"] = message.Content ?? string.Empty,
                            },
                        },
                    });
                    continue;
                }

                if (message.Role == ChatRole.Assistant && message.ToolCalls.Count > 0)
                {
                    var blocks = new JArray();
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        blocks.Add(new JObject { ["type"] = "text", ["text"] = message.Content });
                    }

                    foreach (var call in message.ToolCalls)
                    {
                        blocks.Add(new JObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.Name,
                            ["input"] = ParseArgumentsOrEmpty(call.ArgumentsJson),
                        });
                    }

                    messages.Add(new JObject { ["role"] = "assistant", ["content"] = blocks });
                    continue;
                }

                messages.Add(new JObject
                {
                    ["role"] = RoleName(message.Role),
                    // 带图片时用内容块数组；图片块的 source 是 base64 结构，
                    // 与 OpenAI 的 data URL 形式不同。
                    ["content"] = message.Images.Count > 0
                        ? (JToken)BuildAnthropicContent(message)
                        : message.Content ?? string.Empty,
                });
            }

            var body = new JObject
            {
                ["model"] = request.Model,
                ["messages"] = messages,
                ["stream"] = stream,
                // Anthropic 要求必须显式给出 max_tokens。
                ["max_tokens"] = request.MaxOutputTokens ?? 8192,
            };

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                body["system"] = systemPrompt;
            }

            if (request.IncludeTools)
            {
                var tools = new JArray();
                foreach (var tool in ToolCatalog.All)
                {
                    tools.Add(new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["input_schema"] = JObject.FromObject(tool.Parameters),
                    });
                }

                body["tools"] = tools;
            }

            ApplyAnthropicThinking(body, request);
            return body;
        }

        /// <summary>
        /// Anthropic 的多模态内容块。
        /// 图片用 source.type=base64 加 media_type，不接受 data URL。
        /// 官方建议图片置于文字之前，此处照此排列。
        /// </summary>
        private static JArray BuildAnthropicContent(ChatMessage message)
        {
            var content = new JArray();

            foreach (var image in message.Images)
            {
                content.Add(new JObject
                {
                    ["type"] = "image",
                    ["source"] = new JObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = image.MediaType,
                        ["data"] = image.Base64,
                    },
                });
            }

            if (!string.IsNullOrEmpty(message.Content))
            {
                content.Add(new JObject { ["type"] = "text", ["text"] = message.Content });
            }

            return content;
        }

        /// <summary>
        /// 施加 Anthropic 的思考参数。两代模型互不兼容，必须按模型代际二选一：
        ///
        /// - 新方式（4.6 及更新）：thinking.type=adaptive + output_config.effort。
        ///   4.7 及更新的模型只接受这种方式。
        /// - 旧方式（4.5 及更早）：thinking.type=enabled + budget_tokens。
        ///   在 4.7+ 上会被服务端以 400 拒绝。
        ///
        /// 判断依据是模型名（请求前唯一可得的信息）。判断错不致命：
        /// ChatClient 收到相关 400 时会自动改用另一种方式重试。
        /// </summary>
        private static void ApplyAnthropicThinking(JObject body, ChatRequest request)
        {
            var style = request.AnthropicStyleOverride ?? Thinking.StyleFor(request.Model);

            if (style == AnthropicThinkingStyle.Adaptive)
            {
                if (request.Thinking == ThinkingLevel.Off)
                {
                    body["thinking"] = new JObject { ["type"] = "disabled" };
                }
                else
                {
                    body["thinking"] = new JObject { ["type"] = "adaptive" };
                }

                var effort = Thinking.AnthropicEffort(request.Thinking);
                if (effort != null)
                {
                    body["output_config"] = new JObject { ["effort"] = effort };
                }

                // adaptive 模式不限制 temperature。
                if (request.Temperature.HasValue)
                {
                    body["temperature"] = request.Temperature.Value;
                }

                return;
            }

            var budget = Thinking.AnthropicBudget(request.Thinking, request.MaxOutputTokens ?? 8192);
            if (budget.HasValue)
            {
                body["thinking"] = new JObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = budget.Value,
                };

                // 旧方式启用思考时 temperature 必须为 1，显式传值会被拒绝。
                body.Remove("temperature");
            }
            else if (request.Temperature.HasValue)
            {
                body["temperature"] = request.Temperature.Value;
            }
        }

        // ---- Google Gemini ----

        private static JObject BuildGemini(ChatRequest request)
        {
            var contents = new JArray();
            JObject systemInstruction = null;

            foreach (var message in request.Messages)
            {
                if (message.Role == ChatRole.System)
                {
                    systemInstruction = new JObject
                    {
                        ["parts"] = new JArray { new JObject { ["text"] = message.Content ?? string.Empty } },
                    };
                    continue;
                }

                if (message.Role == ChatRole.Tool)
                {
                    contents.Add(new JObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JArray
                        {
                            new JObject
                            {
                                ["functionResponse"] = new JObject
                                {
                                    ["name"] = message.ToolName ?? string.Empty,
                                    // Gemini 要求响应是对象，字符串需包一层。
                                    ["response"] = new JObject { ["result"] = message.Content ?? string.Empty },
                                },
                            },
                        },
                    });
                    continue;
                }

                if (message.Role == ChatRole.Assistant && message.ToolCalls.Count > 0)
                {
                    var callParts = new JArray();
                    if (!string.IsNullOrEmpty(message.Content))
                    {
                        callParts.Add(new JObject { ["text"] = message.Content });
                    }

                    foreach (var call in message.ToolCalls)
                    {
                        callParts.Add(new JObject
                        {
                            ["functionCall"] = new JObject
                            {
                                ["name"] = call.Name,
                                ["args"] = ParseArgumentsOrEmpty(call.ArgumentsJson),
                            },
                        });
                    }

                    contents.Add(new JObject { ["role"] = "model", ["parts"] = callParts });
                    continue;
                }

                var parts = new JArray();

                // Gemini 的图片用 inlineData，字段名为 mimeType（驼峰）。
                foreach (var image in message.Images)
                {
                    parts.Add(new JObject
                    {
                        ["inlineData"] = new JObject
                        {
                            ["mimeType"] = image.MediaType,
                            ["data"] = image.Base64,
                        },
                    });
                }

                parts.Add(new JObject { ["text"] = message.Content ?? string.Empty });

                contents.Add(new JObject
                {
                    // Gemini 用 model 而非 assistant。
                    ["role"] = message.Role == ChatRole.Assistant ? "model" : "user",
                    ["parts"] = parts,
                });
            }

            var body = new JObject { ["contents"] = contents };

            if (systemInstruction != null)
            {
                body["systemInstruction"] = systemInstruction;
            }

            var generationConfig = new JObject();
            if (request.Temperature.HasValue) { generationConfig["temperature"] = request.Temperature.Value; }
            if (request.MaxOutputTokens.HasValue) { generationConfig["maxOutputTokens"] = request.MaxOutputTokens.Value; }

            // Gemini 的 thinkingConfig 支持两种表达：
            // thinkingLevel（新，minimal/low/medium/high）与 thinkingBudget（旧，token 数）。
            // 这里用 thinkingLevel，并附 includeThoughts 以便展示思考过程。
            // thinkingBudget=0 是官方表达「关闭思考」的方式。
            var geminiLevel = Thinking.GeminiLevel(request.Thinking);
            if (geminiLevel != null)
            {
                generationConfig["thinkingConfig"] = new JObject
                {
                    ["thinkingLevel"] = geminiLevel,
                    ["includeThoughts"] = true,
                };
            }
            else
            {
                generationConfig["thinkingConfig"] = new JObject { ["thinkingBudget"] = 0 };
            }

            if (generationConfig.Count > 0)
            {
                body["generationConfig"] = generationConfig;
            }

            if (request.IncludeTools)
            {
                var declarations = new JArray();
                foreach (var tool in ToolCatalog.All)
                {
                    declarations.Add(new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = SanitizeForGemini(JObject.FromObject(tool.Parameters)),
                    });
                }

                body["tools"] = new JArray { new JObject { ["functionDeclarations"] = declarations } };
            }

            return body;
        }

        /// <summary>
        /// Gemini 的 schema 子集不接受 additionalProperties，
        /// 且不支持 type 为数组的联合类型，需要就近降级为单一类型。
        /// </summary>
        private static JToken SanitizeForGemini(JToken token)
        {
            if (token is JObject obj)
            {
                var result = new JObject();
                foreach (var property in obj.Properties())
                {
                    if (string.Equals(property.Name, "additionalProperties", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (string.Equals(property.Name, "type", StringComparison.Ordinal) &&
                        property.Value is JArray typeArray)
                    {
                        // 取第一个非 null 类型作为代表。
                        string chosen = null;
                        foreach (var item in typeArray)
                        {
                            var name = item.Value<string>();
                            if (!string.Equals(name, "null", StringComparison.OrdinalIgnoreCase))
                            {
                                chosen = name;
                                break;
                            }
                        }

                        result["type"] = chosen ?? "string";
                        continue;
                    }

                    result[property.Name] = SanitizeForGemini(property.Value);
                }

                return result;
            }

            if (token is JArray array)
            {
                var result = new JArray();
                foreach (var item in array)
                {
                    result.Add(SanitizeForGemini(item));
                }

                return result;
            }

            return token;
        }

        // ---- 公共辅助 ----

        private static string RoleName(ChatRole role)
        {
            switch (role)
            {
                case ChatRole.System: return "system";
                case ChatRole.Assistant: return "assistant";
                case ChatRole.Tool: return "tool";
                default: return "user";
            }
        }

        private static JObject ParseArgumentsOrEmpty(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(json);
            }
            catch
            {
                // 模型偶尔会产出不完整的 JSON，此时退化为空参数，
                // 让工具层给出「缺少参数」的明确错误，而不是在此崩溃。
                return new JObject();
            }
        }
    }
}
