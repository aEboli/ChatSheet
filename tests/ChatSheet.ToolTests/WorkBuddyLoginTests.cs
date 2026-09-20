using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChatSheet.AddIn.Providers;
using ChatSheet.AddIn.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    internal static class WorkBuddyLoginTests
    {
        internal static int Run()
        {
            return Task.Run(RunAsync).GetAwaiter().GetResult();
        }

        private static async Task<int> RunAsync()
        {
            var failed = 0;
            void Check(string name, bool ok) { Console.WriteLine((ok ? "通过 " : "失败 ") + name); if (!ok) { failed++; } }
            var directory = Path.Combine(Path.GetTempPath(), "ChatSheet-login-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            WorkBuddyAcpPaths Paths(string scenario) => new WorkBuddyAcpPaths
            {
                NodePath = typeof(WorkBuddyLoginTests).Assembly.Location,
                CliPath = Path.Combine(directory, scenario),
                Scope = WorkBuddyPathScope.International,
            };
            bool Authenticated(WorkBuddyAcpPaths paths) => File.Exists(paths.CliPath + ".requests") &&
                File.ReadAllLines(paths.CliPath + ".requests").Contains("authenticate");
            try
            {
                foreach (var scenario in new[] { "login-authorized", "login-existing", "login-unavailable", "login-unsupported", "login-empty-user", "login-malformed-user" })
                {
                    var paths = Paths(scenario);
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        try { await WorkBuddyAccount.LoginAsync(paths, ConnectionMode.AuthorizedInternational, timeout.Token, null); }
                        catch (ProviderException) { }
                    }
                    Check(scenario + " 不触发退出式授权", !Authenticated(paths));
                }
                try
                {
                    await WorkBuddyAccount.LoginAsync(ConnectionMode.AuthorizedInternational, CancellationToken.None, null, true);
                    Check("旧调用方不能用切换账号绕过保护", false);
                }
                catch (ProviderException ex) { Check("旧调用方不能用切换账号绕过保护", ex.Code == "WORKBUDDY_SWITCH_IN_CLIENT"); }

                foreach (var scenario in new[] { "login-success", "login-pending" })
                {
                    var paths = Paths(scenario);
                    var notices = 0;
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
                    {
                        var result = await WorkBuddyAccount.LoginAsync(paths, ConnectionMode.AuthorizedInternational,
                            timeout.Token, _ => notices++);
                        Check(scenario + " 确认同一来源的新授权并返回模型", result.IsAuthorized && result.CurrentModelId == "new-account-model");
                        Check(scenario + " 重复链接只通知一次且等待不中断", notices == 1 &&
                            File.ReadAllLines(paths.CliPath + ".requests").Count(line => line == "authenticate") == 1);
                    }
                }

                var waitingPaths = Paths("login-wait");
                using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    var page = new TaskCompletionSource<bool>();
                    var pending = WorkBuddyAccount.LoginAsync(waitingPaths, ConnectionMode.AuthorizedInternational, cancel.Token, _ => page.TrySetResult(true));
                    await Task.WhenAny(page.Task, pending);
                    Check("登录期间仍保留等待", page.Task.IsCompleted && !pending.IsCompleted);
                    try
                    {
                        await WorkBuddyAccount.LoginAsync(Paths("login-success-other"), ConnectionMode.AuthorizedInternational, cancel.Token, null);
                        Check("跨面板登录互斥", false);
                    }
                    catch (ProviderException ex) { Check("跨面板登录互斥", ex.Code == "WORKBUDDY_LOGIN_BUSY"); }
                    cancel.Cancel();
                    try { await pending; Check("取消结束等待", false); }
                    catch (OperationCanceledException) { Check("取消结束等待且未伪造成功", !File.Exists(waitingPaths.CliPath + ".state")); }
                }
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    var result = await WorkBuddyAccount.LoginAsync(Paths("login-success-after-cancel"), ConnectionMode.AuthorizedInternational, timeout.Token, null);
                    Check("取消后释放登录锁", result.IsAuthorized);
                }

                var desktop = Path.Combine(directory, "WorkBuddyAI");
                Directory.CreateDirectory(desktop);
                File.WriteAllText(Path.Combine(desktop, "WorkBuddyAI.exe"), "fixture");
                var desktopPaths = new WorkBuddyAcpPaths
                {
                    CliPath = Path.Combine(desktop, "resources", "app.asar.unpacked", "cli", "bin", "codebuddy"),
                    Scope = WorkBuddyPathScope.International,
                };
                Check("账号管理只定位当前来源的官方客户端", WorkBuddyAccount.FindAccountClient(desktopPaths) == Path.Combine(desktop, "WorkBuddyAI.exe"));
                desktopPaths.Scope = WorkBuddyPathScope.Domestic;
                Check("不打开另一产品或猜测独立 CLI 的桌面账号", WorkBuddyAccount.FindAccountClient(desktopPaths) == null &&
                    WorkBuddyAccount.FindAccountClient(Paths("codebuddy.exe")) == null);
            }
            catch (Exception ex) { Check("协议验证异常：" + ex.Message, false); }
            finally { Directory.Delete(directory, true); }
            Console.WriteLine("登录回归失败数：" + failed);
            return failed == 0 ? 0 : 1;
        }

        internal static int RunFixture(string path)
        {
            var scenario = Path.GetFileName(path);
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                var request = JObject.Parse(line);
                var method = request.Value<string>("method");
                File.AppendAllText(path + ".requests", method + "\n");
                var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"] };
                JObject error = null;
                var result = new JObject();
                if (method == "initialize") { result["authMethods"] = new JArray(new JObject { ["id"] = "external" }); }
                if (method == "session/new")
                {
                    if (scenario == "login-authorized" || File.Exists(path + ".state"))
                    {
                        result["models"] = new JObject
                        {
                            ["currentModelId"] = "new-account-model",
                            ["availableModels"] = new JArray(new JObject { ["modelId"] = "new-account-model" }),
                        };
                    }
                    else { error = new JObject { ["code"] = -32000, ["message"] = scenario == "login-unavailable" ? "service unavailable" : "auth_required" }; }
                }
                if (method == "_codebuddy.ai/getUserInfo")
                {
                    if (scenario == "login-empty-user") { result["userInfo"] = new JObject(); }
                    if (scenario == "login-malformed-user") { result["userInfo"] = "invalid"; }
                    if (scenario == "login-existing") { result["userInfo"] = new JObject { ["userId"] = "fixture-user", ["accessToken"] = "fixture-private" }; }
                    if (scenario == "login-unsupported") { error = new JObject { ["code"] = -32601, ["message"] = "Method not supported" }; }
                }
                if (method == "authenticate")
                {
                    var notification = new JObject
                    {
                        ["jsonrpc"] = "2.0", ["method"] = "_codebuddy.ai/authUrl",
                        ["params"] = new JObject { ["authUrl"] = "https://www.workbuddy.ai/login?state=fixture" },
                    };
                    Console.WriteLine(notification.ToString(Formatting.None));
                    Console.WriteLine(notification.ToString(Formatting.None));
                    Console.Out.Flush();
                    if (scenario.StartsWith("login-success", StringComparison.Ordinal) || scenario == "login-pending") { File.WriteAllText(path + ".state", "authorized"); }
                    if (scenario == "login-pending" || scenario == "login-wait") { continue; }
                }
                if (error != null) { response["error"] = error; } else { response["result"] = result; }
                Console.WriteLine(response.ToString(Formatting.None));
                Console.Out.Flush();
            }
            return 0;
        }
    }
}
