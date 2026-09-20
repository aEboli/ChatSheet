using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Tomlyn;
using Tomlyn.Model;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>本地 CLI 的种类。</summary>
    internal enum CliKind
    {
        Auto = 0,
        Claude = 1,
        Codex = 2,
    }

    /// <summary>从本地 CLI 配置中解析出的可用接入信息。</summary>
    internal sealed class CliCredentials
    {
        internal CliKind Source { get; set; }

        internal string DisplayName { get; set; }

        internal ProtocolKind Protocol { get; set; }

        internal string BaseUrl { get; set; }

        internal string Token { get; set; }

        internal string Model { get; set; }

        internal string ConfigPath { get; set; }
    }

    /// <summary>
    /// 读取本机已安装 CLI 的配置，复用其接口地址与令牌。
    ///
    /// 这是「使用电脑本地 CLI 配置」模式的实现：只读取配置文件里的
    /// baseURL 与令牌，然后当作普通接口直连，不启动 CLI 子进程。
    /// 这样流式输出与工具调用完全可控。
    ///
    /// 令牌只在加载项进程内使用，不写入本项目的存储，也不回传给面板。
    /// </summary>
    internal static class LocalCliConfig
    {
        internal static string ClaudeSettingsPath(string homeDir = null)
        {
            return Path.Combine(
                homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "settings.json");
        }

        internal static string CodexAuthPath(string homeDir = null)
        {
            var codexHome = homeDir == null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null;
            var directory = string.IsNullOrWhiteSpace(codexHome)
                ? Path.Combine(homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : codexHome;
            return Path.Combine(directory, "auth.json");
        }

        /// <summary>探测本机可用的 CLI 配置，供设置页展示。</summary>
        internal static IReadOnlyList<CliProbeResult> Probe()
        {
            return new List<CliProbeResult>
            {
                ProbeOne(CliKind.Claude, ClaudeSettingsPath()),
                ProbeOne(CliKind.Codex, CodexAuthPath()),
            };
        }

        private static CliProbeResult ProbeOne(CliKind kind, string path)
        {
            var configPath = kind == CliKind.Codex
                ? Path.Combine(Path.GetDirectoryName(path) ?? ".", "config.toml") : path;
            var result = new CliProbeResult
            {
                Kind = kind,
                DisplayName = kind == CliKind.Claude ? "Claude CLI" : "Codex CLI",
                ConfigPath = File.Exists(configPath) ? configPath : path,
                Exists = File.Exists(path) || File.Exists(configPath),
            };

            if (!result.Exists)
            {
                result.Detail = "未找到配置文件";
                return result;
            }

            try
            {
                var credentials = kind == CliKind.Claude ? ReadClaude(path) : ReadCodex(path);
                result.Usable = true;
                result.Protocol = credentials.Protocol;
                result.BaseUrl = credentials.BaseUrl;
                result.Model = credentials.Model;
                result.Detail = "可用";
            }
            catch (ProviderException ex)
            {
                result.Usable = false;
                result.Detail = ex.Message;
            }
            catch (Exception ex)
            {
                result.Usable = false;
                result.Detail = "读取失败：" + ex.Message;
            }

            return result;
        }

        /// <summary>按指定来源解析凭据。Auto 时优先 Claude，其次 Codex。</summary>
        internal static CliCredentials Resolve(CliKind kind)
        {
            switch (kind)
            {
                case CliKind.Claude:
                    return ReadClaude(ClaudeSettingsPath());
                case CliKind.Codex:
                    return ReadCodex(CodexAuthPath());
                default:
                    var errors = new List<string>();
                    foreach (var candidate in new[] { CliKind.Claude, CliKind.Codex })
                    {
                        try
                        {
                            return Resolve(candidate);
                        }
                        catch (ProviderException ex)
                        {
                            errors.Add($"{candidate}：{ex.Message}");
                        }
                    }

                    throw new ProviderException(
                        "CLI_NOT_AVAILABLE",
                        "未找到可用的本地 CLI 配置。" + string.Join("；", errors));
            }
        }

        private static JObject ReadJson(string path, string label)
        {
            if (!File.Exists(path))
            {
                throw new ProviderException("CLI_CONFIG_MISSING", $"未找到 {label} 配置文件：{path}");
            }

            try
            {
                return JObject.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                throw new ProviderException("CLI_CONFIG_INVALID", $"{label} 配置文件无法解析：{ex.Message}", ex);
            }
        }

        private static CliCredentials ReadClaude(string path)
        {
            var document = ReadJson(path, "Claude CLI");
            var env = document["env"] as JObject;
            if (env == null)
            {
                throw new ProviderException("CLI_CONFIG_INCOMPLETE", "Claude CLI 配置未包含 env 段。");
            }

            var baseUrl = env.Value<string>("ANTHROPIC_BASE_URL");
            var token = env.Value<string>("ANTHROPIC_AUTH_TOKEN") ?? env.Value<string>("ANTHROPIC_API_KEY");

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ProviderException(
                    "CLI_TOKEN_MISSING",
                    "Claude CLI 配置未包含 ANTHROPIC_AUTH_TOKEN 或 ANTHROPIC_API_KEY。" +
                    "若使用订阅登录（OAuth）而非 API 密钥，请改用「自定义接口」模式。");
            }

            // 未显式配置地址时用官方端点。
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "https://api.anthropic.com";
            }

            var model = document.Value<string>("model") ?? env.Value<string>("ANTHROPIC_MODEL");

            return new CliCredentials
            {
                Source = CliKind.Claude,
                DisplayName = "Claude CLI",
                Protocol = ProtocolKind.AnthropicMessages,
                BaseUrl = Protocols.NormalizeBaseUrl(baseUrl, ProtocolKind.AnthropicMessages),
                Token = token.Trim(),
                Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                ConfigPath = path,
            };
        }

        private static CliCredentials ReadCodex(string path)
        {
            var configPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", "config.toml");
            if (!File.Exists(path) && !File.Exists(configPath))
            {
                throw new ProviderException("CLI_CONFIG_MISSING", "未找到 Codex CLI 的 config.toml 或 auth.json：" + Path.GetDirectoryName(path));
            }

            TomlTable config;
            try
            {
                config = File.Exists(configPath) ? Toml.ToModel(File.ReadAllText(configPath)) : new TomlTable();
            }
            catch
            {
                // TOML 解析异常可能包含出错行，其中可能正是凭据；不转发原始异常。
                throw new ProviderException("CLI_CONFIG_INVALID", "Codex config.toml 无法解析，请检查 TOML 格式。");
            }

            if (!string.IsNullOrWhiteSpace(CodexString(config, "profile")))
            {
                throw new ProviderException("CLI_CONFIG_UNSUPPORTED", "当前 Codex 使用配置 profile；请在「自定义接口」中填写该 profile 的连接信息。");
            }
            var providerId = CodexString(config, "model_provider") ?? "openai";
            var providers = config.TryGetValue("model_providers", out var value) ? value as TomlTable : null;
            var provider = providers != null && providers.TryGetValue(providerId, out value) ? value as TomlTable : null;
            if (provider == null && providerId != "openai")
            {
                throw new ProviderException("CLI_CONFIG_INCOMPLETE", "Codex 已选服务商没有对应的 model_providers 配置。");
            }
            provider = provider ?? new TomlTable();
            if (provider.ContainsKey("auth") || provider.ContainsKey("http_headers") || provider.ContainsKey("env_http_headers"))
            {
                throw new ProviderException("CLI_CONFIG_UNSUPPORTED", "当前 Codex 服务商使用额外认证命令或请求头，不能直接复用；请使用「自定义接口」。");
            }

            var wireApi = CodexString(provider, "wire_api") ?? "responses";
            if (wireApi != "responses" && wireApi != "chat")
            {
                throw new ProviderException("CLI_CONFIG_UNSUPPORTED", "Codex 服务商协议不受支持，仅支持 Responses 或 Chat Completions。");
            }
            var protocol = wireApi == "responses" ? ProtocolKind.OpenAiResponses : ProtocolKind.OpenAiChatCompletions;
            var baseUrl = CodexString(provider, "base_url") ?? (providerId == "openai" ? "https://api.openai.com/v1" : null);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ProviderException("CLI_CONFIG_INCOMPLETE", "Codex 自定义服务商未配置 base_url。");
            }
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ProviderException("CLI_CONFIG_INVALID", "Codex 服务商 base_url 必须是有效的 HTTP 或 HTTPS 地址。");
            }

            var requiresOpenAiAuth = providerId == "openai" ||
                (provider.TryGetValue("requires_openai_auth", out value) && value is bool required && required);
            string token;
            if (requiresOpenAiAuth)
            {
                try { token = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)).Value<string>("OPENAI_API_KEY") : null; }
                catch { throw new ProviderException("CLI_CONFIG_INVALID", "Codex auth.json 无法解析，请检查认证文件格式。"); }
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new ProviderException("CLI_TOKEN_MISSING", "当前 Codex 服务商需要 auth.json 中的 OPENAI_API_KEY；ChatGPT 订阅登录或系统凭据库不能直接当作普通接口密钥，请使用「自定义接口」填写 API 凭据。");
                }
            }
            else
            {
                var envKey = CodexString(provider, "env_key");
                token = CodexString(provider, "experimental_bearer_token");
                if (token == null && !string.IsNullOrWhiteSpace(envKey))
                {
                    token = Environment.GetEnvironmentVariable(envKey);
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        throw new ProviderException("CLI_TOKEN_MISSING", "Codex 服务商指定的 env_key 环境变量未设置，请配置后重新启动 Excel/WPS。");
                    }
                }
            }

            return new CliCredentials
            {
                Source = CliKind.Codex,
                DisplayName = "Codex CLI",
                Protocol = protocol,
                // Codex 的 base_url 是完整 API 根地址；根路径也可能直接提供 /responses。
                BaseUrl = baseUrl.TrimEnd('/'),
                Token = token?.Trim(),
                Model = CodexString(config, "model"),
                ConfigPath = File.Exists(configPath) ? configPath : path,
            };
        }

        private static string CodexString(TomlTable table, string key)
        {
            if (!table.TryGetValue(key, out var value)) { return null; }
            if (!(value is string text))
            {
                throw new ProviderException("CLI_CONFIG_INVALID", "Codex 配置字段类型不正确：" + key);
            }
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }

    internal sealed class CliProbeResult
    {
        internal CliKind Kind { get; set; }

        internal string DisplayName { get; set; }

        internal string ConfigPath { get; set; }

        internal bool Exists { get; set; }

        internal bool Usable { get; set; }

        internal ProtocolKind Protocol { get; set; }

        internal string BaseUrl { get; set; }

        internal string Model { get; set; }

        internal string Detail { get; set; }
    }
}
