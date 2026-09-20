using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Providers
{
    internal sealed class WorkBuddyCheckinResult
    {
        public string Status { get; set; }
        public string Date { get; set; }
        public bool Attempted { get; set; }

        internal object Payload() => new
        {
            status = Status,
            date = Date,
            detail = Status == "checked_in" ? "今日已签到"
                : Status == "not_checked_in" ? "今日签到未完成，请稍后刷新确认"
                : Status == "inactive" ? "当前没有可参与的签到活动"
                : Status == "unsupported" ? "当前账号暂不支持每日签到"
                : Status == "unauthorized" ? "登录后自动检查今日签到"
                : "暂时无法确认签到状态，可稍后刷新；如需验证，请到官方页面完成",
        };
    }

    internal sealed class WorkBuddyCheckin
    {
        private readonly string _directory;
        private readonly Func<DateTimeOffset> _now;

        internal WorkBuddyCheckin(string directory = null, Func<DateTimeOffset> now = null)
        {
            _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatSheet", "checkin");
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        internal static string BeijingDate(DateTimeOffset time) => time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        internal static string AccountKey(string userId, string enterpriseId, string authType)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    new JArray(userId, enterpriseId, authType).ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static string ParseStatus(JObject response)
        {
            if (response?["code"]?.Type != JTokenType.Integer || (int)response["code"] != 0) { return "unknown"; }
            var data = response["data"] as JObject;
            if (data?["today_checked_in"]?.Type == JTokenType.Boolean && (bool)data["today_checked_in"]) { return "checked_in"; }
            if (data?["active"]?.Type != JTokenType.Boolean) { return "unknown"; }
            if (!(bool)data["active"]) { return "inactive"; }
            return data["today_checked_in"]?.Type == JTokenType.Boolean ? "not_checked_in" : "unknown";
        }

        internal async Task<WorkBuddyCheckinResult> EnsureAsync(string key, Func<CancellationToken, Task<JObject>> query,
            Func<CancellationToken, Task> claim, bool force, CancellationToken token)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(key ?? "", "^[a-f0-9]{64}$")) { throw new ArgumentException(nameof(key)); }
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, key + ".json");
            // FileShare.None 同时覆盖多个面板、Excel/WPS 进程。文件保留，锁通过关闭句柄释放。
            using (var gate = await AcquireAsync(Path.Combine(_directory, key + ".lock"), token).ConfigureAwait(false))
            {
                var date = BeijingDate(_now());
                WorkBuddyCheckinResult cached = null;
                try { cached = JsonConvert.DeserializeObject<WorkBuddyCheckinResult>(File.ReadAllText(path, Encoding.UTF8)); }
                catch (IOException) { }
                catch (JsonException) { }
                if (cached?.Date != date) { cached = null; }
                if (cached != null && !force && (cached.Status == "checked_in" || cached.Status == "inactive")) { return cached; }

                var result = new WorkBuddyCheckinResult { Date = date, Status = "unknown", Attempted = cached?.Attempted == true || cached?.Status == "checked_in" };
                try
                {
                    result.Status = ParseStatus(await query(token).ConfigureAwait(false));
                    // 查询期间跨日的结果不用于领取或宣称新一天已签到。
                    if (date != BeijingDate(_now())) { return new WorkBuddyCheckinResult { Date = date, Status = "unknown" }; }
                    if (result.Status == "not_checked_in" && !result.Attempted)
                    {
                        result.Attempted = true;
                        Save(path, result); // 必须先落盘；无法保存时不能提交领取。
                        try { await claim(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch { /* 提交可能已经成功，只回查，绝不重发。 */ }
                        result.Status = "unknown";
                        result.Status = ParseStatus(await query(token).ConfigureAwait(false));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { result.Status = "unknown"; }

                if (date != BeijingDate(_now())) { result.Status = "unknown"; }
                Save(path, result);
                return result;
            }
        }

        private static void Save(string path, WorkBuddyCheckinResult result)
        {
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) { File.Replace(temp, path, null); }
            else { File.Move(temp, path); }
        }

        private static async Task<FileStream> AcquireAsync(string path, CancellationToken token)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(150, token).ConfigureAwait(false); }
            }
        }
    }

    internal static class WorkBuddyAccount
    {
        internal static string AuthMethodId(ConnectionMode mode)
        {
            return mode == ConnectionMode.AuthorizedInternational ? "external" : "internal";
        }

        internal static Task LoginAsync(CancellationToken token)
        {
            return LoginAsync(ConnectionMode.Authorized, token, null);
        }

        internal static Task LoginAsync(ConnectionMode mode, CancellationToken token)
        {
            return LoginAsync(mode, token, null);
        }

        internal static async Task<WorkBuddyModelsResult> LoginAsync(ConnectionMode mode, CancellationToken token, Action<string> authUrlCallback,
            bool switchAccount = false)
        {
            if (!mode.IsWorkBuddy())
            {
                throw new ArgumentException(nameof(mode));
            }

            if (switchAccount)
            {
                throw new ProviderException("WORKBUDDY_SWITCH_IN_CLIENT", "请在对应官方客户端切换账号，再返回刷新授权状态。");
            }

            var existing = await WorkBuddyProvider.GetModelsAsync(mode, token).ConfigureAwait(false);
            if (existing.IsAuthorized) { return existing; }
            RequireUnauthorized(existing);

            if (!WorkBuddyProvider.TryFindPaths(mode, out var paths))
            {
                throw new ProviderException(
                    "WORKBUDDY_UNAVAILABLE",
                    mode == ConnectionMode.AuthorizedInternational
                        ? "未找到国际版授权组件，请先安装独立组件或启动国际版 WorkBuddy。"
                        : "未找到国内版授权组件，请先启动或安装国内版 WorkBuddy。");
            }
            var result = await LoginAsync(paths, mode, token, authUrlCallback).ConfigureAwait(false);
            if (result.IsAuthorized) { WorkBuddyProvider.PreferPaths(mode, paths); }
            return result;
        }

        internal static async Task<WorkBuddyModelsResult> LoginAsync(WorkBuddyAcpPaths paths,
            ConnectionMode mode, CancellationToken token, Action<string> authUrlCallback)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatSheet");
            Directory.CreateDirectory(directory);
            FileStream gate;
            try { gate = new FileStream(Path.Combine(directory, "workbuddy-login.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new ProviderException("WORKBUDDY_LOGIN_BUSY", "另一个面板正在登录，请等待完成后刷新状态。"); }
            using (gate)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                // 在锁内重新检查同一来源，防止两个宿主或迟到状态重复授权。
                var existing = await WorkBuddyProvider.GetModelsAsync(paths, timeout.Token).ConfigureAwait(false);
                if (existing.IsAuthorized) { return existing; }
                RequireUnauthorized(existing);
                var authPageReceived = 0;
                using (var connection = new WorkBuddyAccountConnection(paths, mode, url =>
                {
                    Interlocked.Exchange(ref authPageReceived, 1);
                    authUrlCallback?.Invoke(url);
                }))
                {
                    var init = await connection.InitializeAsync(timeout.Token).ConfigureAwait(false);
                    var info = await connection.RequestAsync("_codebuddy.ai/getUserInfo", new JObject(), timeout.Token).ConfigureAwait(false);
                    // 官方未登录响应省略 userInfo 或返回 null；未知结构不能触发退出式授权。
                    var userInfo = info["userInfo"];
                    if (userInfo != null && userInfo.Type != JTokenType.Null)
                    {
                        if (!(userInfo is JObject user) || user["userId"]?.Type != JTokenType.String ||
                            string.IsNullOrWhiteSpace(user.Value<string>("userId")))
                        {
                            throw new ProviderException("WORKBUDDY_ACCOUNT_UNKNOWN", "授权组件返回的账号信息不完整，请在官方客户端确认登录后刷新。");
                        }
                        return await WorkBuddyProvider.GetModelsAsync(paths, timeout.Token).ConfigureAwait(false);
                    }
                    var methods = init["authMethods"] as JArray;
                    var methodId = AuthMethodId(mode);
                    var methodIds = methods == null
                        ? "<none>"
                        : string.Join(",", methods.Select(method => (string)method["id"] ?? "<unknown>"));
                    Log.Info($"WorkBuddy 登录初始化完成：模式={mode} 授权方式={methodIds} 选用={methodId}");
                    if (methods == null || !System.Linq.Enumerable.Any(methods, m => (string)m["id"] == methodId))
                    {
                        throw new ProviderException("WORKBUDDY_LOGIN_UNSUPPORTED",
                            mode == ConnectionMode.AuthorizedInternational
                                ? "当前组件不支持国际版 Google/GitHub 授权，请更新官方组件。"
                                : "当前组件不支持国内浏览器授权，请更新官方组件。");
                    }
                    Log.Info($"WorkBuddy 登录请求开始：模式={mode} 授权方式={methodId}");
                    var authenticate = connection.RequestAsync("authenticate", new JObject { ["methodId"] = methodId }, timeout.Token);
                    try
                    {
                        while (true)
                        {
                            await Task.WhenAny(authenticate, Task.Delay(2000, timeout.Token)).ConfigureAwait(false);
                            timeout.Token.ThrowIfCancellationRequested();
                            if (Volatile.Read(ref authPageReceived) == 0 && !authenticate.IsCompleted) { continue; }
                            // 部分组件在浏览器成功后不结束 authenticate；从同一来源只读确认。
                            var authorization = await WorkBuddyProvider.GetModelsAsync(paths, timeout.Token).ConfigureAwait(false);
                            if (authorization.IsAuthorized)
                            {
                                Log.Info($"WorkBuddy 登录已确认：模式={mode}");
                                return authorization;
                            }
                            if (authenticate.IsCompleted)
                            {
                                await authenticate.ConfigureAwait(false);
                                await Task.Delay(2000, timeout.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        timeout.Cancel();
                        try { await authenticate.ConfigureAwait(false); } catch { /* 回收未完成的 ACP 等待。 */ }
                    }
                }
            }
        }

        private static void RequireUnauthorized(WorkBuddyModelsResult result)
        {
            if (result.State != WorkBuddyAuthorizationState.Unauthorized)
            {
                throw new ProviderException(result.Code ?? "WORKBUDDY_UNAVAILABLE", result.Detail ?? "暂时无法确认账号，请刷新后重试。");
            }
        }

        internal static string FindAccountClient(WorkBuddyAcpPaths paths)
        {
            var cli = paths?.CliPath;
            if (string.IsNullOrEmpty(cli)) { return null; }
            var suffix = Path.Combine("resources", "app.asar.unpacked", "cli", "bin", "codebuddy");
            if (!cli.EndsWith(Path.DirectorySeparatorChar + suffix, StringComparison.OrdinalIgnoreCase)) { return null; }
            var root = cli.Substring(0, cli.Length - suffix.Length).TrimEnd(Path.DirectorySeparatorChar);
            var product = Path.GetFileName(root);
            if (WorkBuddyProvider.ProductScope(product) != paths.Scope || paths.Scope == WorkBuddyPathScope.Unknown) { return null; }
            var executable = Path.Combine(root, product + ".exe");
            return File.Exists(executable) ? executable : null;
        }

        internal static string OpenAccountClient(ConnectionMode mode)
        {
            WorkBuddyProvider.TryFindPaths(mode, out var paths);
            var executable = FindAccountClient(paths);
            if (executable == null)
            {
                return "请在当前授权来源的官方客户端或 CLI 中切换账号，完成后返回点击“刷新模型”。";
            }
            WorkBuddyProcess.StartDetached(executable, "", Path.GetDirectoryName(executable));
            return "已打开官方客户端，请在其中切换账号；返回面板后会自动刷新，也可点击“刷新模型”。";
        }

        internal static async Task<object> RefreshAsync(bool force, CancellationToken token)
        {
            return await RefreshAsync(ConnectionMode.Authorized, force, token).ConfigureAwait(false);
        }

        internal static async Task<object> RefreshAsync(ConnectionMode mode, bool force, CancellationToken token)
        {
            var fallback = new WorkBuddyCheckinResult { Status = "unknown", Date = WorkBuddyCheckin.BeijingDate(DateTimeOffset.UtcNow) };
            if (!mode.IsDomesticWorkBuddy())
            {
                fallback.Status = "unsupported";
                return fallback.Payload();
            }

            var candidates = WorkBuddyProvider.GetPathCandidates(ConnectionMode.Authorized);
            if (candidates.Count == 0)
            {
                fallback.Status = "unauthorized";
                return fallback.Payload();
            }

            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var unsupported = false;
                    foreach (var paths in candidates)
                    {
                        try
                        {
                            JObject info;
                            using (var connection = new WorkBuddyAccountConnection(paths))
                            {
                                await connection.InitializeAsync(timeout.Token).ConfigureAwait(false);
                                var response = await connection.RequestAsync("_codebuddy.ai/getUserInfo", new JObject(), timeout.Token).ConfigureAwait(false);
                                info = response["userInfo"] as JObject;
                            }

                            var uid = (string)info?["userId"];
                            var accessToken = (string)info?["accessToken"];
                            var enterprise = (string)info?["enterpriseId"];
                            var type = (string)info?["authType"];
                            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(accessToken)) { continue; }
                            if ((type != "personal" && type != "local" && type != "internal") || !string.IsNullOrEmpty(enterprise))
                            {
                                unsupported = true;
                                continue;
                            }

                            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
                            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
                            {
                                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                                http.DefaultRequestHeaders.Add("X-User-Id", uid);
                                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                                var result = await new WorkBuddyCheckin().EnsureAsync(WorkBuddyCheckin.AccountKey(uid, enterprise, type),
                                    ct => PostAsync(http, "checkin-activity-status", ct),
                                    async ct => { await PostAsync(http, "daily-checkin", ct).ConfigureAwait(false); },
                                    force, timeout.Token).ConfigureAwait(false);
                                WorkBuddyProvider.PreferPaths(ConnectionMode.Authorized, paths);
                                return result.Payload();
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch
                        {
                            // 当前 CLI 未登录或服务暂时不可用时，继续检查另一套官方 CLI 会话。
                        }
                    }
                    fallback.Status = unsupported ? "unsupported" : "unauthorized";
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (ProviderException ex) when (ex.Code == "WORKBUDDY_NOT_AUTHORIZED") { fallback.Status = "unauthorized"; }
            catch { /* 外部消息可能含凭据，始终返回固定安全文案。 */ }
            return fallback.Payload();
        }

        private static async Task<JObject> PostAsync(HttpClient http, string action, CancellationToken token)
        {
            using (var content = new StringContent("{}", Encoding.UTF8, "application/json"))
            using (var response = await http.PostAsync("https://copilot.tencent.com/v2/billing/meter/" + action, content, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }
    }
}
