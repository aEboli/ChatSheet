using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ChatSheet.AddIn;

namespace ChatSheet.PaneHarness
{
    /// <summary>
    /// 面板测试宿主。不启动 Excel / WPS 也能验证侧边栏控件本身：
    /// WebView2 是否初始化成功、虚拟主机映射能否加载页面、消息桥是否连通。
    ///
    /// 用法：
    ///   ChatSheet.PaneHarness.exe            交互运行，手动查看面板
    ///   ChatSheet.PaneHarness.exe --auto 12  自动运行 12 秒后退出，供脚本化验证
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // 控制台默认按系统 ANSI 代码页输出，中文会显示成乱码。
            TrySetConsoleUtf8();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var autoSeconds = ParseAutoSeconds(args);

            try
            {
                using (var form = BuildForm(autoSeconds))
                {
                    Application.Run(form);
                }

                Console.WriteLine("harness: 正常退出");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("harness: 失败 " + ex);
                return 1;
            }
        }

        private static void TrySetConsoleUtf8()
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch
            {
                // WinExe 在未附加控制台时会失败，忽略即可。
            }
        }

        private static int ParseAutoSeconds(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--auto", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds) && seconds > 0)
                {
                    return seconds;
                }

                return 10;
            }

            return 0;
        }

        private static Form BuildForm(int autoSeconds)
        {
            // 宽度取宿主侧边栏的常见值，便于在真实尺寸下检查布局。
            var form = new Form
            {
                Text = "ChatSheet 面板测试宿主",
                Width = 420,
                Height = 760,
                StartPosition = FormStartPosition.CenterScreen,
                MinimumSize = new Size(300, 400),
            };

            var pane = new TaskPaneControl { Dock = DockStyle.Fill };
            form.Controls.Add(pane);

            if (autoSeconds > 0)
            {
                var timer = new Timer { Interval = autoSeconds * 1000 };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    Console.WriteLine($"harness: 自动模式 {autoSeconds}s 到时，关闭窗口");
                    form.Close();
                };
                form.Shown += (s, e) => timer.Start();
                form.FormClosed += (s, e) => timer.Dispose();
            }

            form.Shown += (s, e) => Console.WriteLine("harness: 窗口已显示，日志见 " + LogDirectory());
            return form;
        }

        private static string LogDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatSheet",
                "logs");
        }
    }
}
