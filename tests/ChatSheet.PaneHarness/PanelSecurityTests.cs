using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ChatSheet.AddIn;
using ChatSheet.AddIn.Bridge;
using Microsoft.Web.WebView2.Core;

namespace ChatSheet.PaneHarness
{
    internal static class PanelSecurityTests
    {
        internal static int Run()
        {
            var failures = 0;
            void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) { failures++; } }
            using (var form = new Form { ClientSize = new System.Drawing.Size(360, 720), ShowInTaskbar = false })
            using (var pane = new TaskPaneControl { Dock = DockStyle.Fill })
            {
                form.Controls.Add(pane);
                form.Shown += async (sender, args) =>
                {
                    try
                    {
                        var core = await WorkBuddyUiTests.WaitForCoreAsync(pane);
                        for (var i = 0; i < 100; i++)
                        {
                            if (await core.ExecuteScriptAsync("document.readyState === 'complete' && !!document.getElementById('composer')") == "true") { break; }
                            await Task.Delay(100);
                        }
                        await core.ExecuteScriptAsync("import('./scripts/bridge.js').then(({request}) => request('ping')).then(r => window.__auditPong = r.pong)");
                        for (var i = 0; i < 50 && await core.ExecuteScriptAsync("window.__auditPong === true") != "true"; i++) { await Task.Delay(100); }
                        Check("本地页面消息桥可用", await core.ExecuteScriptAsync("window.__auditPong === true") == "true");
                        foreach (var uri in new[] { "https://example.invalid/", "https://chatsheet.local:444/", "data:text/html,external", "about:blank" })
                        {
                            var started = new TaskCompletionSource<bool>();
                            EventHandler<CoreWebView2NavigationStartingEventArgs> handler = (s, e) => started.TrySetResult(e.Cancel);
                            core.NavigationStarting += handler;
                            try
                            {
                                core.Navigate(uri);
                                var completed = await Task.WhenAny(started.Task, Task.Delay(5000));
                                Check("顶层导航被取消 " + uri, completed == started.Task && await started.Task);
                                Check("取消后仍保留面板", TaskPaneControl.IsTrustedPanelUri(core.Source) && await core.ExecuteScriptAsync("!!document.getElementById('composer')") == "true");
                            }
                            finally { core.NavigationStarting -= handler; }
                        }

                        var bridge = (HostBridge)typeof(TaskPaneControl).GetField("_bridge", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pane);
                        var invoke = typeof(HostBridge).GetMethod("InvokeOnUiAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                        var ran = false;
                        Task<object> pending = null;
                        using (var queued = new ManualResetEventSlim())
                        {
                            var dispatch = Task.Run(() =>
                            {
                                pending = (Task<object>)invoke.Invoke(bridge, new object[] { (Func<object>)(() => { ran = true; return null; }) });
                                queued.Set();
                            });
                            if (!queued.Wait(TimeSpan.FromSeconds(3))) { throw new TimeoutException("UI 工作未入队"); }
                            bridge.Dispose();
                            await dispatch;
                            var cancelled = false;
                            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
                            await Task.Delay(100);
                            Check("关闭桥后排队的宿主操作被取消", cancelled && !ran);
                        }
                        using (var closing = new TaskPaneControl()) { closing.Dispose(); }
                        await Task.Delay(300);
                        Check("初始化期间关闭控件可正常收束", true);
                    }
                    catch (Exception ex) { Check(ex.GetType().Name + ": " + ex.Message, false); }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            Console.WriteLine("面板边界失败数：" + failures);
            return failures == 0 ? 0 : 1;
        }
    }
}
