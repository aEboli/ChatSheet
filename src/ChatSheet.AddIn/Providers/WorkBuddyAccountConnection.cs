using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>短期 ACP 账号请求。原始消息不记录、不返回面板。</summary>
    internal sealed class WorkBuddyAccountConnection : IDisposable
    {
        private readonly WorkBuddyProcess _process;
        private readonly ConcurrentQueue<string> _output = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _ready = new SemaphoreSlim(0);
        private readonly ConnectionMode _mode;
        private readonly Action<string> _authUrlCallback;
        private int _sequence;
        private string _lastAuthUrl;

        internal WorkBuddyAccountConnection(WorkBuddyAcpPaths paths)
            : this(paths, ConnectionMode.Authorized, null)
        {
        }

        internal WorkBuddyAccountConnection(WorkBuddyAcpPaths paths, ConnectionMode mode)
            : this(paths, mode, null)
        {
        }

        internal WorkBuddyAccountConnection(WorkBuddyAcpPaths paths, ConnectionMode mode, Action<string> authUrlCallback)
        {
            if (!mode.IsWorkBuddy()) { throw new ArgumentException(nameof(mode)); }
            _mode = mode;
            _authUrlCallback = authUrlCallback;
            try
            {
                _process = WorkBuddyProcess.Start(paths, Path.GetDirectoryName(paths.CliPath));
                _ = PumpOutputAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"WorkBuddy ACP 启动失败：模式={mode} 位数={(Environment.Is64BitProcess ? "x64" : "x86")} " +
                    $"CLI={paths.CliPath ?? "<none>"} Node={paths.NodePath ?? "<none>"}", ex);
                _process?.Dispose();
                throw;
            }
        }

        internal Task<JObject> InitializeAsync(CancellationToken token) => RequestAsync("initialize", new JObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JObject(),
            ["clientInfo"] = new JObject
            {
                ["name"] = "ChatSheet",
                ["version"] = typeof(WorkBuddyProvider).Assembly.GetName().Version.ToString(),
            },
        }, token);

        internal async Task<JObject> RequestAsync(string method, JObject parameters, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var id = ++_sequence;
            await WriteAsync(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
            }).ConfigureAwait(false);
            while (true)
            {
                await _ready.WaitAsync(token).ConfigureAwait(false);
                if (!_output.TryDequeue(out var line)) { continue; }
                if (line == null) { throw new ProviderException("WORKBUDDY_UNAVAILABLE", "授权组件已退出，请重新尝试。"); }
                line = line.TrimStart('\uFEFF');
                JObject message;
                try { message = JObject.Parse(line); } catch (JsonException) { continue; }
                var responseId = message["id"];
                if (message["method"] != null)
                {
                    Log.Info($"WorkBuddy ACP 通知：模式={_mode} 方法={message.Value<string>("method")}");
                    if (responseId != null)
                    {
                        var reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = responseId.DeepClone() };
                        if (message.Value<string>("method") == "session/request_permission")
                        {
                            reply["result"] = new JObject { ["outcome"] = new JObject { ["outcome"] = "cancelled" } };
                        }
                        else { reply["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not supported" }; }
                        await WriteAsync(reply).ConfigureAwait(false);
                    }
                    if (message.Value<string>("method") == "_codebuddy.ai/authUrl")
                    {
                        var authUrl = (string)message.SelectToken("params.authUrl") ??
                            (string)message.SelectToken("params.url");
                        if (!IsAllowedAuthUrl(authUrl, _mode))
                        {
                            throw new ProviderException("WORKBUDDY_LOGIN_URL_UNSUPPORTED", "官方授权地址未通过安全校验，未打开浏览器。");
                        }
                        else if (!string.Equals(_lastAuthUrl, authUrl, StringComparison.Ordinal))
                        {
                            _lastAuthUrl = authUrl;
                            TryGetAllowedHost(authUrl, out var authHost);
                            Log.Info($"WorkBuddy ACP 已返回授权页：模式={_mode} 主机={authHost ?? "<unknown>"}");
                            try { _authUrlCallback?.Invoke(authUrl); } catch { /* 面板通知失败不应中断官方授权。 */ }
                        }
                        // 官方 CLI 在发布此通知时自行打开浏览器；这里只通知面板。
                        // 自动启动失败时仍保持 ACP 等待，由用户显式重开同一授权页。
                    }
                    continue;
                }
                if (responseId?.ToString() != id.ToString(System.Globalization.CultureInfo.InvariantCulture)) { continue; }
                if (message["error"] is JObject error)
                {
                    var marker = ((string)error["message"] ?? "").ToLowerInvariant();
                    var unauthorized = (int?)error["code"] == -32000 || marker.Contains("auth") || marker.Contains("login");
                    throw new ProviderException(unauthorized ? "WORKBUDDY_NOT_AUTHORIZED" : "WORKBUDDY_ACP_ERROR",
                        unauthorized ? "请点击浏览器登录完成授权。" : "授权组件未完成请求，请刷新后重试。");
                }
                Log.Info($"WorkBuddy ACP 响应完成：模式={_mode} 请求={method}");
                return message["result"] as JObject ?? throw new ProviderException("WORKBUDDY_ACP_ERROR", "授权组件返回了无法识别的响应，请更新官方组件后重试。");
            }
        }

        internal static bool IsAllowedAuthUrl(string value)
        {
            return TryGetAllowedHost(value, out var host) && IsOfficialHost(host);
        }

        internal static bool IsAllowedAuthUrl(string value, ConnectionMode mode)
        {
            if (!mode.IsWorkBuddy() || !TryGetAllowedHost(value, out var host)) { return false; }
            return mode == ConnectionMode.AuthorizedInternational
                ? IsInternationalHost(host)
                : IsDomesticHost(host);
        }

        private static bool TryGetAllowedHost(string value, out string host)
        {
            host = null;
            if (string.IsNullOrEmpty(value) || value.IndexOfAny(new[] { '\"', '\r', '\n' }) >= 0) { return false; }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) || (uri.Port != 443 && !uri.IsDefaultPort)) { return false; }
            host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            return true;
        }

        private static bool IsOfficialHost(string host)
        {
            var allowed = new[]
            {
                "copilot.tencent.com", "staging-copilot.tencent.com", "www.codebuddy.cn", "staging.codebuddy.cn",
                "www.workbuddy.cn", "staging.workbuddy.cn", "tencent.sso.copilot.tencent.com",
                "tencent.sso.copilot-staging.tencent.com", "tencent.sso.codebuddy.cn", "tencent.staging-sso.codebuddy.cn",
                "codebuddy.ai", "www.codebuddy.ai", "staging.codebuddy.ai", "staging-codebuddy.tencent.com",
                "workbuddy.ai", "www.workbuddy.ai", "staging.workbuddy.ai",
            };
            return Array.Exists(allowed, domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
        }

        private static bool IsDomesticHost(string host)
        {
            return host == "copilot.tencent.com" || host.EndsWith(".copilot.tencent.com", StringComparison.Ordinal) ||
                host == "staging-copilot.tencent.com" ||
                host == "staging-codebuddy.tencent.com" ||
                host == "tencent.sso.copilot-staging.tencent.com" ||
                host == "codebuddy.cn" || host.EndsWith(".codebuddy.cn", StringComparison.Ordinal) ||
                host == "workbuddy.cn" || host.EndsWith(".workbuddy.cn", StringComparison.Ordinal);
        }

        private static bool IsInternationalHost(string host)
        {
            return host == "codebuddy.ai" || host.EndsWith(".codebuddy.ai", StringComparison.Ordinal) ||
                host == "workbuddy.ai" || host.EndsWith(".workbuddy.ai", StringComparison.Ordinal);
        }

        internal static bool TryOpenAuthUrl(string value, ConnectionMode mode)
        {
            if (!TryGetAllowedHost(value, out var host) || !IsAllowedAuthUrl(value, mode)) { return false; }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = value, Verb = "open", UseShellExecute = true });
                Log.Info($"WorkBuddy 授权页已交给默认浏览器：模式={mode} 主机={host}");
                return true;
            }
            catch (Exception primary)
            {
                // WPS/Excel 的后台线程有时无法通过默认 URL 关联启动；Explorer 仍能
                // 将已校验的 HTTPS 地址交给系统默认浏览器。
                try
                {
                    var explorer = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "explorer.exe");
                    WorkBuddyProcess.StartDetached(
                        explorer,
                        "\"" + value + "\"",
                        Path.GetDirectoryName(explorer));
                    Log.Warn($"默认浏览器关联启动失败，已由宿主外 Explorer 打开：模式={mode} 主机={host} 异常={primary.GetType().Name}");
                    return true;
                }
                catch (Exception fallback)
                {
                    Log.Error($"WorkBuddy 授权页启动失败：模式={mode} 主机={host} 首次异常={primary.GetType().Name}", fallback);
                    return false;
                }
            }
        }

        private async Task WriteAsync(JObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None) + "\n");
            await _process.Input.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await _process.Input.FlushAsync().ConfigureAwait(false);
        }

        private async Task PumpOutputAsync()
        {
            try
            {
                string line;
                while ((line = await _process.Output.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    _output.Enqueue(line);
                    _ready.Release();
                }
            }
            catch { }
            finally
            {
                _output.Enqueue(null);
                _ready.Release();
            }
        }

        public void Dispose()
        {
            _process.Dispose();
            // 事件回调可能仍在投递退出消息；由 GC 回收信号量，不在回调期间 Dispose。
        }
    }
}
