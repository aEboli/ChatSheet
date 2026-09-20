using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Providers;

namespace ChatSheet.ToolTests
{
    internal static class CodexConfigTests
    {
        internal static int RunLive()
        {
            return Task.Run(async () =>
            {
                var probe = LocalCliConfig.Probe().Single(p => p.Kind == CliKind.Codex);
                if (!probe.Exists || !probe.Usable)
                {
                    Console.WriteLine("失败 Codex 配置探测：" + probe.Detail);
                    return 1;
                }
                var connection = LocalCliConfig.Resolve(CliKind.Codex);
                using (var client = new ChatClient())
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var models = await client.ListModelsAsync(connection.Protocol, connection.BaseUrl, connection.Token, timeout.Token);
                    Console.WriteLine("协议=" + Protocols.Get(connection.Protocol).Id);
                    Console.WriteLine("地址=" + connection.BaseUrl);
                    Console.WriteLine("配置模型=" + connection.Model);
                    Console.WriteLine("模型数量=" + models.Count);
                    Console.WriteLine("配置模型在目录中=" + models.Contains(connection.Model));
                    Console.WriteLine("探测与连接一致=" + (probe.BaseUrl == connection.BaseUrl && probe.Protocol == connection.Protocol && probe.Model == connection.Model));
                    return models.Count > 0 && models.Contains(connection.Model) ? 0 : 1;
                }
            }).GetAwaiter().GetResult();
        }

        internal static int Run()
        {
            var failed = 0;
            var passed = 0;
            var root = Path.Combine(Path.GetTempPath(), "ChatSheet-CodexConfig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            void Check(string name, bool ok)
            {
                Console.WriteLine((ok ? "通过 " : "失败 ") + name);
                if (ok) { passed++; } else { failed++; }
            }
            void Case(string name, string config, string auth, Func<CliCredentials, bool> expected, string error = null)
            {
                var directory = Path.Combine(root, (passed + failed).ToString());
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "auth.json");
                if (config != null) { File.WriteAllText(Path.Combine(directory, "config.toml"), config); }
                if (auth != null) { File.WriteAllText(path, auth); }
                try
                {
                    var method = typeof(LocalCliConfig).GetMethod("ReadCodex", BindingFlags.Static | BindingFlags.NonPublic);
                    var result = (CliCredentials)method.Invoke(null, new object[] { path });
                    Check(name, error == null && expected(result));
                }
                catch (TargetInvocationException ex)
                {
                    var problem = ex.InnerException as ProviderException;
                    Check(name, error != null && problem?.Code == error &&
                        !problem.ToString().Contains("fixture-secret"));
                }
            }
            const string key = "{\"OPENAI_API_KEY\":\"fixture-api-key\"}";
            const string configHead = "model='fixture-model'\nmodel_provider='selected'\n";
            const string selected = "[model_providers.selected]\nbase_url='http://localhost:8080'\nwire_api='responses'\nrequires_openai_auth=false\n";
            const string envName = "CHATSHEET_TEST_CODEX_CONFIG_KEY";
            var previous = Environment.GetEnvironmentVariable(envName);
            try
            {
                Case("仅 TOML 的 bearer 配置无需 auth.json", configHead + selected + "experimental_bearer_token='fixture-secret'", null,
                    c => c.Token == "fixture-secret" && c.Model == "fixture-model" &&
                        c.Protocol == ProtocolKind.OpenAiResponses && c.BaseUrl == "http://localhost:8080");
                Case("选择指定服务商而非首个地址", configHead +
                    "[model_providers.other]\nbase_url='https://wrong.example/v1'\nexperimental_bearer_token='wrong-key'\n" + selected +
                    "experimental_bearer_token='fixture-secret'", key,
                    c => c.BaseUrl == "http://localhost:8080" && c.Token == "fixture-secret");
                Case("读取当前服务商显示名称", configHead + selected +
                    "name='Moon Stars'\nexperimental_bearer_token='fixture-secret'", null,
                    c => c.ProviderName == "Moon Stars");
                Case("TOML 引号注释和内联表正确解析", "model='模型甲'\nmodel_provider='selected' # 选择来源\n" +
                    "model_providers.selected={base_url='https://proxy.example/custom/v2/', experimental_bearer_token='fixture-secret#part'}", null,
                    c => c.Model == "模型甲" && c.Token == "fixture-secret#part" && c.BaseUrl == "https://proxy.example/custom/v2");
                Environment.SetEnvironmentVariable(envName, "fixture-env-key");
                Case("读取服务商指定环境变量", configHead + selected + "env_key='" + envName + "'", null,
                    c => c.Token == "fixture-env-key");
                Environment.SetEnvironmentVariable(envName, null);
                Case("指定环境变量缺失不借用其他凭据", configHead + selected + "env_key='" + envName + "'", key, null, "CLI_TOKEN_MISSING");
                Case("需要 OpenAI 认证时读取 API key", configHead + selected.Replace("requires_openai_auth=false", "requires_openai_auth=true") + "env_key='" + envName + "'", key,
                    c => c.Token == "fixture-api-key");
                Case("旧 auth.json API key 保持可用", null, key,
                    c => c.Token == "fixture-api-key" && c.BaseUrl == "https://api.openai.com/v1" && c.Protocol == ProtocolKind.OpenAiResponses);
                Case("订阅凭据不当作 API key", null, "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"fixture-secret\"}}", null, "CLI_TOKEN_MISSING");
                Case("缺失的选中服务商不使用其他地址", configHead + "[model_providers.other]\nbase_url='https://wrong.example'", key, null, "CLI_CONFIG_INCOMPLETE");
                Case("不支持的协议明确报错", configHead + selected.Replace("responses", "unknown") + "experimental_bearer_token='fixture-secret'", null, null, "CLI_CONFIG_UNSUPPORTED");
                Case("TOML 解析错误不会泄露原文", "model_provider='selected'\nexperimental_bearer_token='fixture-secret", key, null, "CLI_CONFIG_INVALID");
                Case("缺少地址不将自定义凭据发送到默认服务", configHead + "[model_providers.selected]\nexperimental_bearer_token='fixture-secret'", null, null, "CLI_CONFIG_INCOMPLETE");
                Case("不执行外部认证命令", configHead + selected + "[model_providers.selected.auth]\ncommand='fixture-secret'", null, null, "CLI_CONFIG_UNSUPPORTED");
                Case("无认证自定义服务商可用", configHead + selected, null,
                    c => string.IsNullOrEmpty(c.Token) && c.BaseUrl == "http://localhost:8080");
                Case("保留兼容 Chat Completions 配置", configHead + selected.Replace("responses", "chat") + "experimental_bearer_token='fixture-secret'", null,
                    c => c.Protocol == ProtocolKind.OpenAiChatCompletions);
                Case("两份配置都不存在才显示缺失", null, null, null, "CLI_CONFIG_MISSING");
            }
            finally
            {
                Environment.SetEnvironmentVariable(envName, previous);
                var expectedRoot = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "ChatSheet-CodexConfig-");
                if (!Path.GetFullPath(root).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("验证目录超出临时目录范围。");
                }
                Directory.Delete(root, true);
            }
            Console.WriteLine($"Codex 配置验证：通过 {passed}，失败 {failed}");
            return failed == 0 ? 0 : 1;
        }
    }
}
