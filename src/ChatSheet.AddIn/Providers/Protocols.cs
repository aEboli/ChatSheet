using System;
using System.Collections.Generic;
using System.Linq;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>受支持的接口协议。</summary>
    internal enum ProtocolKind
    {
        OpenAiChatCompletions = 0,
        OpenAiResponses = 1,
        AnthropicMessages = 2,
        GoogleGemini = 3,
    }

    internal sealed class ProtocolDefinition
    {
        internal ProtocolDefinition(
            ProtocolKind kind,
            string id,
            string label,
            string chatPath,
            string modelsPath)
        {
            Kind = kind;
            Id = id;
            Label = label;
            ChatPath = chatPath;
            ModelsPath = modelsPath;
        }

        internal ProtocolKind Kind { get; }

        internal string Id { get; }

        internal string Label { get; }

        internal string ChatPath { get; }

        /// <summary>模型列表端点。为空表示该协议不提供模型发现。</summary>
        internal string ModelsPath { get; }
    }

    internal static class Protocols
    {
        internal static readonly IReadOnlyList<ProtocolDefinition> All = new List<ProtocolDefinition>
        {
            new ProtocolDefinition(
                ProtocolKind.OpenAiChatCompletions,
                "openai-chat-completions",
                "OpenAI Chat Completions（兼容最广）",
                "/chat/completions",
                "/models"),
            new ProtocolDefinition(
                ProtocolKind.OpenAiResponses,
                "openai-responses",
                "OpenAI Responses",
                "/responses",
                "/models"),
            new ProtocolDefinition(
                ProtocolKind.AnthropicMessages,
                "anthropic-messages",
                "Anthropic Messages",
                "/messages",
                "/models"),
            new ProtocolDefinition(
                ProtocolKind.GoogleGemini,
                "google-gemini",
                "Google Gemini",
                // Gemini 把模型名放在路径里，实际端点在构造时拼接。
                "/models",
                "/models"),
        };

        internal const ProtocolKind Default = ProtocolKind.OpenAiChatCompletions;

        internal static ProtocolDefinition Get(ProtocolKind kind)
        {
            return All.First(p => p.Kind == kind);
        }

        internal static bool TryParse(string id, out ProtocolKind kind)
        {
            var match = All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                kind = Default;
                return false;
            }

            kind = match.Kind;
            return true;
        }

        /// <summary>
        /// 规范化用户填写的 baseURL。
        ///
        /// 用户常见的几种输入都要能接受：带或不带 /v1、带或不带尾斜杠、
        /// 甚至直接粘贴完整的 /chat/completions 端点。这里统一收敛成 API 根地址，
        /// 否则拼接后会出现 /v1/v1/chat/completions 这类错误路径。
        /// </summary>
        internal static string NormalizeBaseUrl(string raw, ProtocolKind kind)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ProviderException("BASE_URL_REQUIRED", "接口地址不能为空。");
            }

            var text = raw.Trim();
            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                text = "https://" + text;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                throw new ProviderException("BASE_URL_INVALID", $"接口地址「{raw}」无法解析。");
            }

            var path = uri.AbsolutePath.TrimEnd('/');

            // 用户可能整段粘贴了具体端点，去掉已知后缀还原成根地址。
            foreach (var suffix in new[]
            {
                "/chat/completions", "/responses", "/messages", "/completions", "/models",
            })
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(0, path.Length - suffix.Length);
                    break;
                }
            }

            // 各协议的默认版本段：用户没写时补上，写了就尊重原样。
            if (path.Length == 0)
            {
                switch (kind)
                {
                    case ProtocolKind.AnthropicMessages:
                        path = "/v1";
                        break;
                    case ProtocolKind.GoogleGemini:
                        path = "/v1beta";
                        break;
                    default:
                        path = "/v1";
                        break;
                }
            }

            return uri.GetLeftPart(UriPartial.Authority) + path;
        }

        internal static string BuildChatEndpoint(ProtocolKind kind, string baseUrl, string model, bool stream)
        {
            var root = baseUrl.TrimEnd('/');
            if (kind == ProtocolKind.GoogleGemini)
            {
                // Gemini 的模型名与动作都在路径中。
                var action = stream ? "streamGenerateContent" : "generateContent";
                var suffix = stream ? "?alt=sse" : string.Empty;
                return $"{root}/models/{Uri.EscapeDataString(model ?? string.Empty)}:{action}{suffix}";
            }

            return root + Get(kind).ChatPath;
        }

        internal static string BuildModelsEndpoint(ProtocolKind kind, string baseUrl)
        {
            var definition = Get(kind);
            if (string.IsNullOrEmpty(definition.ModelsPath))
            {
                return null;
            }

            return baseUrl.TrimEnd('/') + definition.ModelsPath;
        }

        /// <summary>构造认证头。各协议的头名与格式不同，写错会直接 401。</summary>
        internal static IReadOnlyDictionary<string, string> AuthHeaders(ProtocolKind kind, string token)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(token))
            {
                return headers;
            }

            switch (kind)
            {
                case ProtocolKind.AnthropicMessages:
                    headers["x-api-key"] = token;
                    // 缺少此头 Anthropic 会拒绝请求。
                    headers["anthropic-version"] = "2023-06-01";
                    break;
                case ProtocolKind.GoogleGemini:
                    headers["x-goog-api-key"] = token;
                    break;
                default:
                    headers["Authorization"] = "Bearer " + token;
                    break;
            }

            return headers;
        }
    }

    /// <summary>接入层的可预期错误，消息面向用户，需可直接展示。</summary>
    internal sealed class ProviderException : Exception
    {
        internal ProviderException(string code, string message, Exception inner = null)
            : base(message, inner)
        {
            Code = code;
        }

        internal string Code { get; }

        /// <summary>
        /// 服务端原文，不含本地拼装的任何提示。判定「这条错误在说谁」只能读它。
        ///
        /// 与 Message 分开是必须的：Message 尾部会拼上 HintFor 给用户的建议，
        /// 而 404 那句是「请检查接口地址与模型名是否正确」——含「模型名」二字。
        /// 拿 Message 去认「错误有没有点名这个模型」，我们自己的提示就会把每一个
        /// 404 都认成假阳性，于是地址填错也会给每个模型判死刑。
        ///
        /// 读不出来时为空。空不得退回去读 Message，只能判未知。
        /// </summary>
        internal string Detail { get; set; }

        /// <summary>
        /// 服务端 Retry-After 给出的建议等待时长，没有则为空。
        /// 限流场景下按它等待比本地退避更准，也更不容易被继续拒绝。
        /// </summary>
        internal TimeSpan? RetryAfter { get; set; }
    }
}
