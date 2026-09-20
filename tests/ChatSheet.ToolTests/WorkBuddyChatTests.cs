using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ChatSheet.AddIn.Agent;
using ChatSheet.AddIn.Providers;
using ChatSheet.AddIn.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    internal static class WorkBuddyChatTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            try { Task.Run(() => RunAsync(report)).GetAwaiter().GetResult(); }
            catch (Exception ex) { report("ACP 进程协议测试", false, ex.Message); }
        }

        private static WorkBuddyChatClient Fixture(string scenario) => new WorkBuddyChatClient(
            new WorkBuddyAcpPaths
            {
                NodePath = typeof(WorkBuddyChatTests).Assembly.Location,
                CliPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, scenario),
            });

        private static async Task RunAsync(Action<string, bool, string> report)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var client = Fixture("echo"))
            {
                var request = new ChatRequest { Model = "hy3" };
                var system = ChatMessage.FromSystem("系统指令：中文校验");
                request.Messages.Add(system);
                request.Messages.Add(ChatMessage.FromUser("你好"));
                var events = new List<ChatEvent>();
                await client.StreamAsync(request, e => { events.Add(e); return Task.CompletedTask; }, timeout.Token);
                var first = JObject.Parse(events.First(e => e.Kind == ChatEventKind.TextDelta).Text);
                report("ACP 选择模型并传递中文系统指令和用户消息",
                    first.Value<string>("model") == "hy3" && first["prompt"].ToString().Contains("你好") &&
                    first["prompt"].ToString().Contains("系统指令：中文校验"), first.ToString());
                report("ACP 正确处理流式思考、用量、完成和字符串权限请求",
                    events.Any(e => e.Kind == ChatEventKind.ThinkingDelta && e.Text == "思考") &&
                    events.Any(e => e.Kind == ChatEventKind.Usage && e.PromptTokens == 12 && e.CompletionTokens == 3) &&
                    events.Last().Kind == ChatEventKind.Completed && first.Value<bool>("permissionCancelled"), "事件不完整");

                request.Messages.Add(ChatMessage.FromAssistant("已经请求工具"));
                request.Messages.Add(ChatMessage.FromTextProtocolToolResult("read_range", "单元格结果=42"));
                system.Content = "系统指令已更新";
                events.Clear();
                await client.StreamAsync(request, e => { events.Add(e); return Task.CompletedTask; }, timeout.Token);
                var second = JObject.Parse(events.First(e => e.Kind == ChatEventKind.TextDelta).Text);
                var followup = second["prompt"].ToString();
                report("ACP 同一会话只续传工具结果和系统更新",
                    first.Value<int>("pid") == second.Value<int>("pid") && second.Value<int>("turn") == 2 &&
                    followup.Contains("单元格结果=42") && followup.Contains("系统指令已更新") &&
                    !followup.Contains("你好") && !followup.Contains("已经请求工具"), followup);
            }

            var processId = 0;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var client = Fixture("hang"))
            {
                var request = new ChatRequest { Model = "hy3" };
                request.Messages.Add(ChatMessage.FromUser("停止测试"));
                var cancelled = false;
                try
                {
                    await client.StreamAsync(request, e =>
                    {
                        if (e.Kind == ChatEventKind.TextDelta)
                        {
                            processId = JObject.Parse(e.Text).Value<int>("pid");
                            timeout.Cancel();
                        }
                        return Task.CompletedTask;
                    }, timeout.Token);
                }
                catch (OperationCanceledException) { cancelled = true; }
                report("ACP 用户取消及时结束请求", cancelled && processId > 0, "未收到取消");
            }
            var stopped = false;
            try { using (var process = Process.GetProcessById(processId)) { stopped = process.HasExited; } }
            catch (ArgumentException) { stopped = true; }
            report("ACP 取消后释放自身进程", stopped, "进程仍在运行");

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var client = Fixture("auth"))
            {
                try
                {
                    await client.StreamAsync(new ChatRequest(), e => Task.CompletedTask, timeout.Token);
                    report("ACP 未登录返回明确错误", false, "意外成功");
                }
                catch (ProviderException ex)
                {
                    report("ACP 未登录返回明确错误且不透传原始内容",
                        ex.Code == "WORKBUDDY_NOT_AUTHORIZED" && !ex.Message.Contains("fixture-private"), ex.Message);
                }
            }
        }

        private enum LiveStatus
        {
            Authorized,
            Blocked,
            Failed,
        }

        private sealed class LiveContext
        {
            internal LiveStatus Status { get; set; }

            internal string Model { get; set; }
        }

        internal static int RunLive(Action<string, bool, string> report)
        {
            return RunLive(ConnectionMode.Authorized, report);
        }

        /// <summary>
        /// 实机回归入口。只读取 ACP 授权状态，不主动打开浏览器；未授权返回 2，
        /// 让调用脚本把「需要用户登录」与测试代码失败（1）区分开。
        /// </summary>
        internal static int RunLive(ConnectionMode mode, Action<string, bool, string> report)
        {
            try
            {
                var context = Task.Run(() => RunLiveAsync(mode, report)).GetAwaiter().GetResult();
                if (context.Status == LiveStatus.Blocked) { return 2; }
                if (context.Status != LiveStatus.Authorized) { return 1; }
                RunExcelLive(mode, context.Model, report);
                return 0;
            }
            catch (Exception ex)
            {
                report("WorkBuddy 实机调用", false, ex.Message);
                return 1;
            }
        }

        private static void RunExcelLive(
            ConnectionMode mode,
            string model,
            Action<string, bool, string> report)
        {
            dynamic excel = null, books = null, workbook = null, sheet = null, cell = null;
            try
            {
                excel = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application", true));
                excel.Visible = false;
                excel.DisplayAlerts = false;
                books = excel.Workbooks;
                workbook = books.Add();
                sheet = excel.ActiveSheet;
                cell = sheet.Range["A1"];
                using (var ui = new Control())
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                {
                    var handle = ui.Handle;
                    var runner = new AgentRunner(() => (object)excel, action =>
                    {
                        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                        ui.BeginInvoke(new Action(() =>
                        {
                            try { completion.SetResult(action()); }
                            catch (Exception ex) { completion.SetException(ex); }
                        }));
                        return completion.Task;
                    });
                    var settings = new Settings
                    {
                        Mode = mode, Model = model,
                        Approval = ApprovalPolicy.PerWrite,
                    };
                    var approvals = 0;
                    var edition = mode == ConnectionMode.AuthorizedInternational ? "WorkBuddy 国际版" : "WorkBuddy";
                    Console.WriteLine($"        在临时 Excel 工作簿验证 {edition} {model} 工具调用…");
                    var task = runner.RunAsync(
                        "这是一个空白临时测试工作簿。请用 write_range 工具把当前工作表 A1 写为 ACP中文验证42。必须实际调用工具，收到成功结果后只回复验证完成。", settings,
                        update => Task.CompletedTask,
                        (tool, args, impact) =>
                        {
                            approvals++;
                            return Task.FromResult(new ApprovalDecision { Approved = true });
                        }, timeout.Token);
                    while (!task.IsCompleted)
                    {
                        Application.DoEvents();
                        Thread.Sleep(15);
                    }
                    task.GetAwaiter().GetResult();
                    string value = Convert.ToString(cell.Value2);
                    report($"{edition} 经 AgentRunner 审批并实际写入临时 Excel A1",
                        approvals > 0 && value == "ACP中文验证42", $"审批={approvals} A1={value}");
                    Console.WriteLine($"        临时工作簿 A1={value}，审批次数={approvals}");
                }
            }
            finally
            {
                try { if (workbook != null) { workbook.Close(false); } }
                finally { if (excel != null) { excel.Quit(); } }
                foreach (object item in new object[] { cell, sheet, workbook, books, excel })
                {
                    if (item != null && Marshal.IsComObject(item)) { Marshal.FinalReleaseComObject(item); }
                }
            }
        }

        private static async Task<LiveContext> RunLiveAsync(
            ConnectionMode mode,
            Action<string, bool, string> report)
        {
            var context = new LiveContext { Status = LiveStatus.Failed };
            var edition = mode == ConnectionMode.AuthorizedInternational ? "WorkBuddy 国际版" : "WorkBuddy";
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                var models = await WorkBuddyProvider.GetModelsAsync(mode, timeout.Token);
                if (!models.IsAuthorized)
                {
                    var blocked = models.State == WorkBuddyAuthorizationState.Unauthorized;
                    Console.WriteLine($"  {(blocked ? "阻塞" : "失败")}  {edition} 实机授权目录：{models.Code ?? models.State.ToString()} — {models.Detail}");
                    context.Status = blocked ? LiveStatus.Blocked : LiveStatus.Failed;
                    return context;
                }

                var available = models.Models ?? new List<WorkBuddyModelInfo>();
                var model = mode == ConnectionMode.Authorized
                    ? "hy3"
                    : available.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ModelId))?.ModelId;
                var hasModel = !string.IsNullOrWhiteSpace(model) &&
                    (mode != ConnectionMode.Authorized || available.Any(item => item.ModelId == model));
                report($"{edition} 实机授权目录包含 {model ?? "可用模型"}", hasModel, models.Detail);
                if (!hasModel) { return context; }
                context.Status = LiveStatus.Authorized;
                context.Model = model;

                using (var client = WorkBuddyProvider.CreateChatClient(mode))
                {
                    var request = new ChatRequest { Model = model, IncludeTools = false };
                    request.Messages.Add(ChatMessage.FromUser("请只回复 OK。"));
                    var text = new StringBuilder();
                    await client.StreamAsync(request, e =>
                    {
                        if (e.Kind == ChatEventKind.TextDelta) { text.Append(e.Text); }
                        return Task.CompletedTask;
                    }, timeout.Token);
                    report($"{edition} {model} 实际返回正文", text.ToString().IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0, text.ToString());
                    Console.WriteLine($"        {model} 回复：" + text);

                    request.Messages.Add(ChatMessage.FromAssistant(text.ToString()));
                    request.Messages.Add(ChatMessage.FromTextProtocolToolResult("read_range", "{\"ok\":true,\"value\":\"ACP中文验证42\"}"));
                    request.Messages.Add(ChatMessage.FromUser("只回复刚才工具返回的 value 原文。"));
                    text.Clear();
                    await client.StreamAsync(request, e =>
                    {
                        if (e.Kind == ChatEventKind.TextDelta) { text.Append(e.Text); }
                        return Task.CompletedTask;
                    }, timeout.Token);
                    report($"{edition} {model} 同会话读取工具结果", text.ToString().Contains("ACP中文验证42"), text.ToString());
                    Console.WriteLine("        工具结果回复：" + text);
                }

                var connection = new Settings { Mode = mode, Model = model }.ResolveConnection();
                var verdict = await ModelProbe.ProbeAsync(connection, model, null, timeout.Token);
                report($"{edition} 模型试一下通过 ACP 判为可用", verdict == AvailabilityVerdict.Available, verdict.ToString());
                return context;
            }
        }

        // 子进程模拟 ACP 的另一端；与真实客户端之间仅通过 UTF-8 标准流通信。
        internal static int RunFixture(string scenarioPath)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            var scenario = Path.GetFileName(scenarioPath);
            var model = "";
            var turn = 0;
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                var message = JObject.Parse(line);
                var method = message.Value<string>("method");
                if (method == "session/cancel") { return 0; }
                var id = message["id"];
                if (scenario == "auth")
                {
                    Write(new JObject { ["id"] = id, ["error"] = new JObject { ["code"] = -32000, ["message"] = "fixture-private" } });
                    continue;
                }
                if (method == "session/set_model") { model = message["params"].Value<string>("modelId"); }
                if (method == "session/prompt")
                {
                    // WorkBuddy 会连续推送较大的会话通知；超过管道缓冲区也不能阻塞正文或取消。
                    for (var i = 0; i < 64; i++)
                    {
                        Update("session_info_update", new JObject { ["text"] = new string('测', 1024) });
                    }
                    Write(new JObject { ["id"] = "permission-1", ["method"] = "session/request_permission", ["params"] = new JObject() });
                    var permission = JObject.Parse(Console.ReadLine());
                    var cancelled = permission.SelectToken("result.outcome.outcome")?.Value<string>() == "cancelled";
                    Write(new JObject { ["id"] = "unrelated", ["result"] = new JObject() });
                    Update("agent_thought_chunk", new JObject { ["type"] = "text", ["text"] = "思考" });
                    var content = new JObject
                    {
                        ["pid"] = Process.GetCurrentProcess().Id, ["turn"] = ++turn,
                        ["model"] = model, ["prompt"] = message["params"]["prompt"], ["permissionCancelled"] = cancelled,
                    };
                    Update("agent_message_chunk", new JObject { ["type"] = "text", ["text"] = content.ToString(Formatting.None) });
                    if (scenario == "hang") { continue; }
                    Write(new JObject { ["method"] = "session/update", ["params"] = new JObject
                    {
                        ["update"] = new JObject { ["sessionUpdate"] = "usage_update", ["inputTokens"] = 12, ["outputTokens"] = 3 },
                    } });
                }
                Write(new JObject { ["id"] = id, ["result"] = new JObject { ["sessionId"] = "fixture", ["stopReason"] = "end_turn" } });
            }
            return 0;
        }

        private static void Update(string kind, JObject content) => Write(new JObject
        {
            ["method"] = "session/update",
            ["params"] = new JObject { ["update"] = new JObject { ["sessionUpdate"] = kind, ["content"] = content } },
        });

        private static void Write(JObject message)
        {
            message["jsonrpc"] = "2.0";
            Console.WriteLine(message.ToString(Formatting.None));
            Console.Out.Flush();
        }
    }
}
