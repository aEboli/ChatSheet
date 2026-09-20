using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Providers;
using ChatSheet.AddIn.Storage;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    internal static class WorkBuddyAccountTests
    {
        internal static int Run(string command)
        {
            try
            {
                return Task.Run(async () =>
                {
                    if (command == "--workbuddy-install")
                    {
                        await WorkBuddyRuntime.InstallAsync(Console.WriteLine, CancellationToken.None);
                        Console.WriteLine(JObject.FromObject(WorkBuddyRuntime.Status()).ToString());
                        return WorkBuddyRuntime.FindNativePath() != null ? 0 : 1;
                    }
                    if (command == "--workbuddy-login-live" || command == "--workbuddy-login-live-intl")
                    {
                        var mode = command == "--workbuddy-login-live-intl"
                            ? ConnectionMode.AuthorizedInternational
                            : ConnectionMode.Authorized;
                        Console.WriteLine($"登录探测：模式={mode} 位数={(Environment.Is64BitProcess ? "x64" : "x86")}");
                        var authUrlReceived = false;
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                        {
                            try
                            {
                                await WorkBuddyAccount.LoginAsync(mode, timeout.Token, url =>
                                {
                                    authUrlReceived = true;
                                    Console.WriteLine("收到授权页：" + new Uri(url).Host);
                                });
                            }
                            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                            {
                                Console.WriteLine("登录探测超时：20 秒内未完成；收到授权页=" + authUrlReceived);
                                return 3;
                            }
                        }
                        Console.WriteLine((authUrlReceived ? "浏览器授权成功：" : "已复用本机登录状态，未触发重新授权：") + mode);
                        return 0;
                    }
                    if (command == "--workbuddy-fallback-live" || command == "--workbuddy-fallback-live-intl")
                    {
                        var mode = command == "--workbuddy-fallback-live-intl"
                            ? ConnectionMode.AuthorizedInternational
                            : ConnectionMode.Authorized;
                        var result = await WorkBuddyProvider.GetModelsAsync(mode, CancellationToken.None);
                        WorkBuddyProvider.TryFindPaths(mode, out var selectedPaths);
                        Console.WriteLine(JObject.FromObject(new
                        {
                            mode = mode.ToString(),
                            status = result.State.ToString(),
                            code = result.Code,
                            count = result.Models?.Count ?? 0,
                            modelsWithMultiplier = result.Models?.Count(model => model.CreditMultiplier != null) ?? 0,
                            configDirectory = Path.GetFileName(selectedPaths?.ConfigDirectory),
                            detail = result.Detail,
                        }).ToString());
                        // 未授权不是测试代码失败：它是实机前置条件未满足，使用独立退出码
                        // 让脚本可以区分「需要登录」与「组件损坏/协议失败」。
                        if (result.State == WorkBuddyAuthorizationState.Unauthorized) { return 2; }
                        return result.IsAuthorized ? 0 : 1;
                    }
                    if (command == "--workbuddy-account-live" || command == "--workbuddy-account-live-intl")
                    {
                        var mode = command == "--workbuddy-account-live-intl"
                            ? ConnectionMode.AuthorizedInternational
                            : ConnectionMode.Authorized;
                        var checkin = await WorkBuddyAccount.RefreshAsync(mode, true, CancellationToken.None);
                        Console.WriteLine(JObject.FromObject(checkin).ToString());
                        // 国际版明确不走国内签到，unsupported 是预期结果而不是失败。
                        if (mode == ConnectionMode.AuthorizedInternational &&
                            JObject.FromObject(checkin).Value<string>("status") == "unsupported") { return 0; }
                        return mode == ConnectionMode.AuthorizedInternational ? 1 : 0;
                    }
                    return await TestsAsync();
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("验证失败：" + ex.Message);
                return 1;
            }
        }

        private static JObject Response(bool signed) => JObject.FromObject(new { code = 0, data = new { active = true, today_checked_in = signed } });

        private static async Task<int> TestsAsync()
        {
            var failed = 0;
            void Check(string name, bool ok) { Console.WriteLine((ok ? "通过 " : "失败 ") + name); if (!ok) { failed++; } }
            var temp = Path.Combine(Path.GetTempPath(), "ChatSheet-checkin-" + Guid.NewGuid().ToString("N"));
            var now = new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);
            var key = WorkBuddyCheckin.AccountKey("account-A", "", "local");
            var other = WorkBuddyCheckin.AccountKey("account-B", "", "local");
            try
            {
                var store = new WorkBuddyCheckin(temp, () => now);
                var queries = 0;
                var claims = 0;
                Task<JObject> Checked(CancellationToken _) { queries++; return Task.FromResult(Response(true)); }
                Task Claim(CancellationToken _) { claims++; return Task.CompletedTask; }
                var result = await store.EnsureAsync(key, Checked, Claim, false, CancellationToken.None);
                await new WorkBuddyCheckin(temp, () => now).EnsureAsync(key, Checked, Claim, false, CancellationToken.None);
                Check("已签到账户当日只查询一次，不重复领取", result.Status == "checked_in" && queries == 1 && claims == 0);

                now = now.AddDays(1);
                queries = 0;
                Task<JObject> Pending(CancellationToken _) { queries++; return Task.FromResult(Response(claims > 0)); }
                var concurrent = await Task.WhenAll(
                    store.EnsureAsync(key, Pending, Claim, false, CancellationToken.None),
                    new WorkBuddyCheckin(temp, () => now).EnsureAsync(key, Pending, Claim, false, CancellationToken.None));
                Check("跨日与多个实例只领取一次，回查后确认", claims == 1 && queries == 2 && concurrent[0].Status == "checked_in" && concurrent[1].Status == "checked_in");
                Check("账号指纹不会暴露账号或混用不同账户", key != other && key.Length == 64 && !key.Contains("account"));
                Check("缓存不含账号或认证值", !File.ReadAllText(Path.Combine(temp, key + ".json")).Contains("account-A") &&
                    !File.ReadAllText(Path.Combine(temp, key + ".json")).ToLowerInvariant().Contains("token"));

                queries = 0;
                claims = 0;
                Task<JObject> Unknown(CancellationToken _) { queries++; return Task.FromResult(new JObject()); }
                result = await store.EnsureAsync(other, Unknown, Claim, false, CancellationToken.None);
                Check("状态未知时不领取、不显示已签到", result.Status == "unknown" && claims == 0 && queries == 1);
                result = await store.EnsureAsync(other, _ => Task.FromResult(Response(false)),
                    _ => { claims++; throw new TimeoutException(); }, true, CancellationToken.None);
                Check("提交超时后回查，仍未签到如实显示", result.Status == "not_checked_in" && result.Attempted && claims == 1);
                result = await store.EnsureAsync(other, _ => Task.FromResult(Response(false)), Claim, true, CancellationToken.None);
                Check("手动刷新不能重发当天已提交的领取", result.Status == "not_checked_in" && claims == 1);
                result = await store.EnsureAsync(other, Checked, Claim, false, CancellationToken.None);
                Check("迟到的成功通过自动回查恢复已签到，不重复领取", result.Status == "checked_in" && claims == 1);

                var retryKey = WorkBuddyCheckin.AccountKey("retry-account", "", "local");
                var retryQueries = 0;
                var retryClaims = 0;
                Task RetryClaim(CancellationToken _) { retryClaims++; return Task.CompletedTask; }
                result = await store.EnsureAsync(retryKey, _ =>
                {
                    retryQueries++;
                    throw new IOException("模拟首次查询网络失败");
                }, RetryClaim, false, CancellationToken.None);
                Check("首次网络失败只记录未知状态，不领取", result.Status == "unknown" && !result.Attempted && retryClaims == 0);
                Task<JObject> Recovered(CancellationToken _) { retryQueries++; return Task.FromResult(Response(retryClaims > 0)); }
                var recovered = await Task.WhenAll(
                    store.EnsureAsync(retryKey, Recovered, RetryClaim, false, CancellationToken.None),
                    new WorkBuddyCheckin(temp, () => now).EnsureAsync(retryKey, Recovered, RetryClaim, false, CancellationToken.None));
                Check("未知缓存不会阻塞当天自动签到，多个面板恢复后只领取一次",
                    recovered.All(value => value.Status == "checked_in") && retryClaims == 1 && retryQueries == 3);

                Check("未知协议字段不被推断为已签到", WorkBuddyCheckin.ParseStatus(JObject.Parse("{code:0,data:{active:true}}")) == "unknown" &&
                    WorkBuddyCheckin.ParseStatus(JObject.Parse("{code:0,data:{active:true,today_checked_in:'false'}}")) == "unknown");
                Check("活动关闭不会领取", WorkBuddyCheckin.ParseStatus(JObject.Parse("{code:0,data:{active:false,today_checked_in:false}}")) == "inactive");
                Check("北京时间午夜日期准确", WorkBuddyCheckin.BeijingDate(new DateTimeOffset(2026, 9, 18, 16, 0, 0, TimeSpan.Zero)) == "2026-09-19");
                now = now.AddDays(1);
                claims = 0;
                result = await store.EnsureAsync(key, _ => { now = now.AddDays(1); return Task.FromResult(Response(false)); }, Claim, false, CancellationToken.None);
                Check("查询途中跨日不领取、不沿用旧结果", result.Status == "unknown" && claims == 0 && result.Date != WorkBuddyCheckin.BeijingDate(now));

                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try { await store.EnsureAsync(key, Checked, Claim, false, cancelled.Token); Check("取消中断签到", false); }
                    catch (OperationCanceledException) { Check("取消中断签到", true); }
                }
                Check("官方 headless 包名与入口文件已固定", WorkBuddyRuntime.PackageName == "codebuddy-code-headless_Windows_x86_64.zip" &&
                    WorkBuddyRuntime.HeadlessExecutableName == "codebuddy-headless.exe");
                Check("校验和只接受精确文件名和完整 SHA256", WorkBuddyRuntime.ParseChecksum(new string('a', 64) + "  " + WorkBuddyRuntime.PackageName + "\n", WorkBuddyRuntime.PackageName) == new string('a', 64) &&
                    WorkBuddyRuntime.ParseChecksum("invalid  " + WorkBuddyRuntime.PackageName, WorkBuddyRuntime.PackageName) == null);
                Check("本地应用数据目录支持宿主环境变量回退",
                    WorkBuddyRuntime.ResolveLocalApplicationDataDirectory(
                        string.Empty, @"C:\Fallback\Local", string.Empty) == @"C:\Fallback\Local" &&
                    WorkBuddyRuntime.ResolveLocalApplicationDataDirectory(
                        string.Empty, string.Empty, @"C:\Users\Tester") == @"C:\Users\Tester\AppData\Local");
                var nativeRoot = Path.Combine(temp, "native-root");
                var nativeVersion = Path.Combine(nativeRoot, "codebuddy", "Data", "versions", "2.154.0");
                Directory.CreateDirectory(nativeVersion);
                var nativeExecutable = Path.Combine(nativeVersion, "codebuddy.exe");
                File.WriteAllBytes(nativeExecutable, new[] { (byte)'M', (byte)'Z' });
                Check("国际版版本目录可被稳定发现",
                    string.Equals(WorkBuddyRuntime.FindNativePath(nativeRoot), nativeExecutable,
                        StringComparison.OrdinalIgnoreCase));
                var managedRoot = Path.Combine(temp, "managed-root");
                var managedExecutable = Path.Combine(managedRoot, "ChatSheet", "components", "codebuddy.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(managedExecutable));
                File.WriteAllBytes(managedExecutable, new[] { (byte)'M', (byte)'Z' });
                Check("WPS 隔离宿主可使用 ChatSheet 组件桥接入口",
                    string.Equals(WorkBuddyRuntime.FindNativePath(managedRoot), managedExecutable,
                        StringComparison.OrdinalIgnoreCase));
                Check("国际版使用 external 授权，国内版使用 internal 授权", WorkBuddyAccount.AuthMethodId(ConnectionMode.AuthorizedInternational) == "external" &&
                    WorkBuddyAccount.AuthMethodId(ConnectionMode.Authorized) == "internal");
                Check("国内版与国际版产品路径分类不混用", WorkBuddyProvider.ProductScope("WorkBuddy") == WorkBuddyPathScope.Domestic &&
                    WorkBuddyProvider.ProductScope("WorkBuddyAI") == WorkBuddyPathScope.International &&
                    WorkBuddyProvider.ProductScope("CodeBuddy") == WorkBuddyPathScope.International &&
                    WorkBuddyProvider.ProductScope("OtherProduct") == WorkBuddyPathScope.Unknown);
                var creditCases = new[] { "x0.34 credits", " X3.31 CREDITS ", "x0.00", "x1.39", "", "x-1 credits", "xNaN credits", "x1.0 unexpected", "x1e2 credits" };
                var creditModels = WorkBuddyProvider.ParseModels(new JObject
                {
                    ["models"] = new JObject
                    {
                        ["availableModels"] = new JArray(creditCases.Select((credits, index) => new JObject
                        {
                            ["modelId"] = "credit-" + index,
                            ["_meta"] = new JObject { ["credits"] = credits },
                        })),
                    },
                });
                Check("国际版 ACP credits 单位倍率可解析且零倍率保留",
                    creditModels.Select(model => model.CreditMultiplier).SequenceEqual(
                        new[] { "0.34x", "3.31x", "0.00x", "1.39x", null, null, null, null, null }));
                var domesticPaths = WorkBuddyProvider.GetPathCandidates(ConnectionMode.Authorized);
                var internationalPaths = WorkBuddyProvider.GetPathCandidates(ConnectionMode.AuthorizedInternational);
                Check("国内模式不会候选国际 ACP 路径", !domesticPaths.Any(path => path.Scope == WorkBuddyPathScope.International));
                Check("国际模式不会候选国内 ACP 路径", !internationalPaths.Any(path => path.Scope == WorkBuddyPathScope.Domestic));
                Check("国内候选不含 WorkBuddyAI 桌面端", !domesticPaths.Any(path =>
                    path.CliPath.IndexOf("\\WorkBuddyAI\\", StringComparison.OrdinalIgnoreCase) >= 0));
                var internationalCheckin = JObject.FromObject(await WorkBuddyAccount.RefreshAsync(
                    ConnectionMode.AuthorizedInternational, true, CancellationToken.None));
                Check("国际版不调用国内签到并明确返回不支持", internationalCheckin.Value<string>("status") == "unsupported");
                var native = new WorkBuddyAcpPaths { CliPath = typeof(WorkBuddyAccountTests).Assembly.Location };
                var launch = WorkBuddyRuntime.StartInfo(native, AppDomain.CurrentDomain.BaseDirectory);
                Check("原生组件不使用 Node 启动", launch.FileName == native.CliPath && launch.Arguments.StartsWith("--acp "));
                var configured = new WorkBuddyAcpPaths
                {
                    NodePath = native.CliPath,
                    CliPath = native.CliPath,
                    ConfigDirectory = Path.Combine(WorkBuddyProvider.UserProfileDirectory(), ".workbuddy"),
                };
                var configuredLaunch = WorkBuddyRuntime.StartInfo(configured, AppDomain.CurrentDomain.BaseDirectory);
                Check("ACP 进程继承对应产品配置目录",
                    configuredLaunch.Environment["WORKBUDDY_CONFIG_DIR"] == configured.ConfigDirectory &&
                    configuredLaunch.Environment["CODEBUDDY_CONFIG_DIR"] == configured.ConfigDirectory &&
                    configuredLaunch.Environment["WORKBUDDY_DATA_FOLDER_NAME"] == ".workbuddy");
                var legacyEnvironmentField = typeof(System.Diagnostics.ProcessStartInfo).GetField(
                    "environmentVariables",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var legacyEnvironment = legacyEnvironmentField?.GetValue(configuredLaunch)
                    as System.Collections.Specialized.StringDictionary;
                Check(".NET Framework 实际启动环境继承产品配置目录",
                    legacyEnvironment != null &&
                    legacyEnvironment["WORKBUDDY_CONFIG_DIR"] == configured.ConfigDirectory &&
                    legacyEnvironment["CODEBUDDY_CONFIG_DIR"] == configured.ConfigDirectory &&
                    legacyEnvironment["WORKBUDDY_DATA_FOLDER_NAME"] == ".workbuddy");
                Check("32 位宿主仍能解析用户目录", !string.IsNullOrWhiteSpace(WorkBuddyProvider.UserProfileDirectory()) &&
                    WorkBuddyProvider.UserProfileDirectory().EndsWith("Administrator", StringComparison.OrdinalIgnoreCase));
                var domesticConfig = WorkBuddyProvider.ConfigDirectoryForCliPath(
                    @"C:\\Users\\Administrator\\AppData\\Local\\Programs\\WorkBuddy\\resources\\app.asar.unpacked\\cli\\bin\\codebuddy",
                    WorkBuddyPathScope.Domestic);
                var internationalConfig = WorkBuddyProvider.ConfigDirectoryForCliPath(
                    @"C:\\Users\\Administrator\\AppData\\Local\\Programs\\CodeBuddy\\resources\\app.asar.unpacked\\cli\\bin\\codebuddy",
                    WorkBuddyPathScope.International);
                var desktopInternationalConfig = WorkBuddyProvider.ConfigDirectoryForCliPath(
                    @"C:\Program Files\WorkBuddyAI\resources\app.asar.unpacked\cli\bin\codebuddy",
                    WorkBuddyPathScope.International);
                Check("国际版桌面端沿用 .workbuddy-ai，独立 CLI 沿用 .codebuddy",
                    desktopInternationalConfig.EndsWith(".workbuddy-ai", StringComparison.OrdinalIgnoreCase) &&
                    desktopInternationalConfig != internationalConfig);
                Check("国内/国际 ACP 使用各自配置目录", domesticConfig.EndsWith(".workbuddy", StringComparison.OrdinalIgnoreCase) &&
                    internationalConfig.EndsWith(".codebuddy", StringComparison.OrdinalIgnoreCase) && domesticConfig != internationalConfig);
                Check("浏览器授权只允许官方 HTTPS 域名", WorkBuddyAccountConnection.IsAllowedAuthUrl("https://login.copilot.tencent.com/oauth/authorize?state=test") &&
                    !WorkBuddyAccountConnection.IsAllowedAuthUrl("http://copilot.tencent.com/login") &&
                    !WorkBuddyAccountConnection.IsAllowedAuthUrl("https://copilot.tencent.com.evil.invalid/login") &&
                    !WorkBuddyAccountConnection.IsAllowedAuthUrl("https://user@copilot.tencent.com/login") &&
                    !WorkBuddyAccountConnection.IsAllowedAuthUrl("https://www.codebuddy.ai/login?next=\"bad\"") &&
                    WorkBuddyAccountConnection.IsAllowedAuthUrl("https://www.codebuddy.ai/login?platform=CLI") &&
                    WorkBuddyAccountConnection.IsAllowedAuthUrl("https://staging-codebuddy.tencent.com/login?platform=CLI") &&
                    WorkBuddyAccountConnection.IsAllowedAuthUrl("https://login.workbuddy.ai/oauth") &&
                    !WorkBuddyAccountConnection.IsAllowedAuthUrl("http://www.codebuddy.ai/login"));
                Check("国内/国际授权域名按模式隔离", WorkBuddyAccountConnection.IsAllowedAuthUrl(
                        "https://staging-codebuddy.tencent.com/login", ConnectionMode.Authorized) &&
                    !WorkBuddyAccountConnection.IsAllowedAuthUrl(
                        "https://staging-codebuddy.tencent.com/login", ConnectionMode.AuthorizedInternational) &&
                    WorkBuddyAccountConnection.IsAllowedAuthUrl(
                        "https://www.codebuddy.ai/login", ConnectionMode.AuthorizedInternational) &&
                    !WorkBuddyAccountConnection.IsAllowedAuthUrl(
                        "https://www.codebuddy.ai/login", ConnectionMode.Authorized));
                Check("重新打开授权页仍按模式和 HTTPS 校验",
                    !WorkBuddyAccountConnection.TryOpenAuthUrl("https://www.codebuddy.ai/login", ConnectionMode.Authorized) &&
                    !WorkBuddyAccountConnection.TryOpenAuthUrl("javascript:alert(1)", ConnectionMode.AuthorizedInternational));
                using (var connection = new WorkBuddyAccountConnection(native))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    var initialized = await connection.InitializeAsync(timeout.Token);
                    Check("实际原生进程 ACP 管道可初始化", initialized != null);
                }
                var brokerProcessId = 0;
                using (var broker = WorkBuddyProcess.Start(native, AppDomain.CurrentDomain.BaseDirectory, true))
                {
                    brokerProcessId = broker.BrokerProcessId;
                    var request = new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = 991,
                        ["method"] = "initialize",
                        ["params"] = new JObject(),
                    };
                    var bytes = System.Text.Encoding.UTF8.GetBytes(request.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                    await broker.Input.WriteAsync(bytes, 0, bytes.Length);
                    await broker.Input.FlushAsync();
                    var responseTask = broker.Output.ReadLineAsync();
                    var completed = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromSeconds(10)));
                    var response = completed == responseTask ? await responseTask : null;
                    Check("WMI 命名管道代理可完成 ACP 初始化",
                        broker.IsBrokered && response != null && JObject.Parse(response).Value<int?>("id") == 991);
                }
                await Task.Delay(200);
                var brokerExited = false;
                try
                {
                    using (var process = System.Diagnostics.Process.GetProcessById(brokerProcessId))
                    {
                        brokerExited = process.HasExited;
                    }
                }
                catch (ArgumentException)
                {
                    brokerExited = true;
                }
                Check("释放代理会回收 WMI 进程树", brokerProcessId > 0 && brokerExited);
                Console.WriteLine("每日签到验证失败数：" + failed);
                return failed == 0 ? 0 : 1;
            }
            finally { if (Directory.Exists(temp)) { Directory.Delete(temp, true); } }
        }
    }
}
