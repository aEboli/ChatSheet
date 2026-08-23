using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

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
            return Path.Combine(
                homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "auth.json");
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
            var result = new CliProbeResult
            {
                Kind = kind,
                DisplayName = kind == CliKind.Claude ? "Claude CLI" : "Codex CLI",
                ConfigPath = path,
                Exists = File.Exists(path),
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
            var document = ReadJson(path, "Codex CLI");
            var token = document.Value<string>("OPENAI_API_KEY");

            if (string.IsNullOrWhiteSpace(token))
            {
                var mode = document.Value<string>("auth_mode");
                throw new ProviderException(
                    "CLI_TOKEN_MISSING",
                    "Codex CLI 配置未包含 OPENAI_API_KEY" +
                    (string.IsNullOrEmpty(mode) ? "。" : $"（当前 auth_mode={mode}）。") +
                    "若使用订阅登录而非 API 密钥，请改用「自定义接口」模式。");
            }

            // Codex 的接口地址可能记录在同目录 config.toml 里；
            // 这里只做轻量提取，取不到就用官方端点。
            var baseUrl = TryReadCodexBaseUrl(Path.Combine(Path.GetDirectoryName(path) ?? ".", "config.toml"))
                ?? "https://api.openai.com";

            return new CliCredentials
            {
                Source = CliKind.Codex,
                DisplayName = "Codex CLI",
                Protocol = ProtocolKind.OpenAiChatCompletions,
                BaseUrl = Protocols.NormalizeBaseUrl(baseUrl, ProtocolKind.OpenAiChatCompletions),
                Token = token.Trim(),
                Model = null,
                ConfigPath = path,
            };
        }

        /// <summary>
        /// 从 config.toml 里提取 base_url。
        /// 刻意不引入 TOML 解析库：只需要一个键，正则足够且不增加依赖。
        /// </summary>
        private static string TryReadCodexBaseUrl(string tomlPath)
        {
            try
            {
                if (!File.Exists(tomlPath))
                {
                    return null;
                }

                foreach (var line in File.ReadAllLines(tomlPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var match = System.Text.RegularExpressions.Regex.Match(
                        trimmed,
                        @"^base_url\s*=\s*[""']([^""']+)[""']");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取 Codex config.toml 失败：" + ex.Message);
            }

            return null;
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
