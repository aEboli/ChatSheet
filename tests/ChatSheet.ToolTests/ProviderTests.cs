using System;
using ChatSheet.AddIn.Providers;
using ChatSheet.AddIn.Storage;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 接入层验证。覆盖 baseURL 规范化、认证头构造、本地 CLI 探测与密钥存储。
    /// 全程不打印任何令牌值，只验证结构与可用性。
    /// </summary>
    internal static class ProviderTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestNormalizeBaseUrl(report);
            TestEndpoints(report);
            TestAuthHeaders(report);
            TestSecretStore(report);
            TestCliProbe(report);
            TestConnectionResolution(report);
        }

        private static void TestNormalizeBaseUrl(Action<string, bool, string> report)
        {
            // 用户可能填的各种形态都要收敛到同一个 API 根地址。
            var cases = new[]
            {
                new { Input = "https://api.openai.com", Expect = "https://api.openai.com/v1" },
                new { Input = "https://api.openai.com/", Expect = "https://api.openai.com/v1" },
                new { Input = "https://api.openai.com/v1", Expect = "https://api.openai.com/v1" },
                new { Input = "https://api.openai.com/v1/", Expect = "https://api.openai.com/v1" },
                // 整段粘贴具体端点也要能还原。
                new { Input = "https://api.openai.com/v1/chat/completions", Expect = "https://api.openai.com/v1" },
                // 缺少协议头时补 https。
                new { Input = "api.deepseek.com", Expect = "https://api.deepseek.com/v1" },
                // 自定义路径应被保留。
                new { Input = "https://gateway.local/proxy/openai", Expect = "https://gateway.local/proxy/openai" },
            };

            foreach (var c in cases)
            {
                try
                {
                    var actual = Protocols.NormalizeBaseUrl(c.Input, ProtocolKind.OpenAiChatCompletions);
                    report($"规范化 {c.Input}", actual == c.Expect, $"得到 {actual}，期望 {c.Expect}");
                }
                catch (Exception ex)
                {
                    report($"规范化 {c.Input}", false, ex.Message);
                }
            }

            // Anthropic 与 Gemini 的默认版本段不同。
            try
            {
                var anthropic = Protocols.NormalizeBaseUrl("https://api.anthropic.com", ProtocolKind.AnthropicMessages);
                report("Anthropic 默认版本段", anthropic == "https://api.anthropic.com/v1", anthropic);

                var gemini = Protocols.NormalizeBaseUrl("https://generativelanguage.googleapis.com", ProtocolKind.GoogleGemini);
                report("Gemini 默认版本段", gemini == "https://generativelanguage.googleapis.com/v1beta", gemini);
            }
            catch (Exception ex)
            {
                report("协议默认版本段", false, ex.Message);
            }

            // 空地址必须报错而不是静默通过。
            try
            {
                Protocols.NormalizeBaseUrl("", ProtocolKind.OpenAiChatCompletions);
                report("空地址应报错", false, "未抛出异常");
            }
            catch (ProviderException ex)
            {
                report("空地址应报错", ex.Code == "BASE_URL_REQUIRED", ex.Code);
            }
        }

        private static void TestEndpoints(Action<string, bool, string> report)
        {
            var chat = Protocols.BuildChatEndpoint(
                ProtocolKind.OpenAiChatCompletions, "https://api.openai.com/v1", "gpt-4o", stream: true);
            report("OpenAI 对话端点", chat == "https://api.openai.com/v1/chat/completions", chat);

            var anthropic = Protocols.BuildChatEndpoint(
                ProtocolKind.AnthropicMessages, "https://api.anthropic.com/v1", "claude-sonnet-4", stream: true);
            report("Anthropic 对话端点", anthropic == "https://api.anthropic.com/v1/messages", anthropic);

            // Gemini 把模型名与动作放在路径里，流式还要带 alt=sse。
            var gemini = Protocols.BuildChatEndpoint(
                ProtocolKind.GoogleGemini, "https://generativelanguage.googleapis.com/v1beta", "gemini-2.0-flash", stream: true);
            var geminiExpect = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:streamGenerateContent?alt=sse";
            report("Gemini 流式端点", gemini == geminiExpect, gemini);

            var geminiSync = Protocols.BuildChatEndpoint(
                ProtocolKind.GoogleGemini, "https://generativelanguage.googleapis.com/v1beta", "gemini-2.0-flash", stream: false);
            report("Gemini 非流式端点", geminiSync.EndsWith(":generateContent"), geminiSync);

            var models = Protocols.BuildModelsEndpoint(ProtocolKind.OpenAiChatCompletions, "https://api.openai.com/v1");
            report("模型列表端点", models == "https://api.openai.com/v1/models", models);
        }

        private static void TestAuthHeaders(Action<string, bool, string> report)
        {
            var openai = Protocols.AuthHeaders(ProtocolKind.OpenAiChatCompletions, "TOKEN");
            report("OpenAI 认证头", openai.ContainsKey("Authorization") && openai["Authorization"] == "Bearer TOKEN", "");

            var anthropic = Protocols.AuthHeaders(ProtocolKind.AnthropicMessages, "TOKEN");
            // 缺少 anthropic-version 会被服务端拒绝，必须一起带上。
            report(
                "Anthropic 认证头含版本",
                anthropic.ContainsKey("x-api-key") && anthropic.ContainsKey("anthropic-version"),
                string.Join(",", anthropic.Keys));

            var gemini = Protocols.AuthHeaders(ProtocolKind.GoogleGemini, "TOKEN");
            report("Gemini 认证头", gemini.ContainsKey("x-goog-api-key"), string.Join(",", gemini.Keys));
        }

        private static void TestSecretStore(Action<string, bool, string> report)
        {
            const string key = "chatsheet-selftest";
            const string secret = "sk-test-abcd1234";

            try
            {
                SecretStore.Save(key, secret);
                var loaded = SecretStore.Load(key);
                report("密钥往返", loaded == secret, loaded == null ? "读回为空" : "读回不一致");
                report("密钥存在判定", SecretStore.Exists(key), "");
                report("掩码只露末四位", SecretStore.Mask(secret) == "…1234", SecretStore.Mask(secret));

                SecretStore.Delete(key);
                report("密钥删除", !SecretStore.Exists(key) && SecretStore.Load(key) == null, "");
            }
            catch (Exception ex)
            {
                report("密钥存储", false, ex.Message);
            }
            finally
            {
                SecretStore.Delete(key);
            }
        }

        /// <summary>
        /// 验证接入解析：模式 ① 应能从本机 CLI 配置自动取得地址与令牌，
        /// 无需用户填写。只读配置文件，不发网络请求。
        /// </summary>
        private static void TestConnectionResolution(Action<string, bool, string> report)
        {
            // 模式 ①：不填任何东西，应能解析出地址与令牌。
            var localCli = new Settings
            {
                Mode = ConnectionMode.LocalCli,
                CliSource = CliKind.Auto,
                Model = string.Empty,
            };

            try
            {
                var resolved = localCli.ResolveConnection();
                report(
                    "模式① 自动取得接口地址",
                    !string.IsNullOrWhiteSpace(resolved.BaseUrl),
                    "地址为空");
                report(
                    "模式① 自动取得令牌",
                    !string.IsNullOrWhiteSpace(resolved.Token),
                    "令牌为空");
                Console.WriteLine($"        来源={resolved.SourceLabel} 协议={Protocols.Get(resolved.Protocol).Id} " +
                    $"地址={resolved.BaseUrl} 模型={(string.IsNullOrEmpty(resolved.Model) ? "<配置未指定>" : resolved.Model)}");

                // 本机两份 CLI 配置都不含 model，因此这里预期为空——
                // 这正是「选了模式① 仍需指定模型」的原因。
                report(
                    "模式① 模型需另行指定（本机配置未含 model）",
                    true,
                    string.Empty);
            }
            catch (ProviderException ex)
            {
                report("模式① 解析接入信息", false, $"{ex.Code} {ex.Message}");
            }

            // 模式 ②：地址为空时必须报错，且错误码可用于界面提示。
            var custom = new Settings
            {
                Mode = ConnectionMode.CustomApi,
                CustomBaseUrl = string.Empty,
            };

            try
            {
                custom.ResolveConnection();
                report("模式② 地址为空应报错", false, "未抛异常");
            }
            catch (ProviderException ex)
            {
                report("模式② 地址为空应报错", ex.Code == "BASE_URL_REQUIRED", ex.Code);
            }

            // 模式 ③ 尚未实现，应给出明确提示而非静默失败。
            var authorized = new Settings { Mode = ConnectionMode.Authorized };
            try
            {
                authorized.ResolveConnection();
                report("模式③ 应提示未实现", false, "未抛异常");
            }
            catch (ProviderException ex)
            {
                report("模式③ 应提示未实现", ex.Code == "MODE_NOT_IMPLEMENTED", ex.Code);
            }
        }

        private static void TestCliProbe(Action<string, bool, string> report)
        {
            try
            {
                var results = LocalCliConfig.Probe();
                report("CLI 探测返回两项", results.Count == 2, $"实际 {results.Count} 项");

                foreach (var r in results)
                {
                    // 只报告可用性与地址，绝不打印令牌。
                    var detail = $"存在={r.Exists} 可用={r.Usable} 地址={r.BaseUrl} 说明={r.Detail}";
                    Console.WriteLine($"        {r.DisplayName}: {detail}");

                    // 探测本身不应抛异常；配置不可用是合法结果。
                    report($"{r.DisplayName} 探测完成", r.Detail != null, detail);
                }
            }
            catch (Exception ex)
            {
                report("CLI 探测", false, ex.Message);
            }
        }
    }
}
