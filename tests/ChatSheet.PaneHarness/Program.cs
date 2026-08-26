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
    ///   ChatSheet.PaneHarness.exe --theme    在真实 WebView2 里验证主题切换后退出
    ///   ChatSheet.PaneHarness.exe --capture 目录  两套主题各截对话页与设置页后退出
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

            // 主题检查自带时限，不额外等待。
            var themeCheck = Array.Exists(
                args,
                a => string.Equals(a, "--theme", StringComparison.OrdinalIgnoreCase));

            try
            {
                if (themeCheck)
                {
                    return RunThemeCheck();
                }

                var captureDir = ParseCaptureDir(args);
                if (captureDir != null)
                {
                    return RunCapture(captureDir);
                }

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

        /// <summary>取 --capture 后面的目录。没给这个开关时返回 null。</summary>
        private static string ParseCaptureDir(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--capture", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return args[i + 1];
                }

                return Path.Combine(Path.GetTempPath(), "chatsheet-capture");
            }

            return null;
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

        /// <summary>
        /// 在真实 WebView2 里验证主题切换。
        ///
        /// 为什么非要在这里跑一遍：Node 侧的检查只看得到源码文本，
        /// 看不出 var(--x) 到底算出了什么颜色。变量名写错、或深色调色板漏了一项时，
        /// CSS 里那行 var(--x) 看着完全正常，浏览器却静默退回默认色——
        /// 只有真实排版后的计算值能暴露这种情况。
        ///
        /// 同时验证面板之外那圈宿主控件的底色：它不受页面 CSS 管辖，
        /// 深色下漏涂就是紧贴页面的一块白边。
        /// </summary>
        private static int RunThemeCheck()
        {
            var failed = 0;

            void Assert(bool condition, string message, string detail = "")
            {
                if (condition)
                {
                    Console.WriteLine("  通过  " + message);
                }
                else
                {
                    failed++;
                    Console.WriteLine("  失败  " + message);
                    if (detail.Length > 0) { Console.WriteLine("        " + detail); }
                }
            }

            using (var form = new Form
            {
                Text = "ChatSheet 主题检查",
                Width = 420,
                Height = 760,
                StartPosition = FormStartPosition.CenterScreen,
            })
            {
                var pane = new TaskPaneControl { Dock = DockStyle.Fill };
                form.Controls.Add(pane);

                form.Shown += async (s, e) =>
                {
                    try
                    {
                        // 等页面加载完。WebView2 初始化与首屏都要时间，
                        // 读到「尚未就绪」就再等一会儿，最多等到超时。
                        var state = string.Empty;
                        for (var i = 0; i < 40; i++)
                        {
                            await System.Threading.Tasks.Task.Delay(500);
                            state = pane.ReadThemeState();
                            if (state.StartsWith("theme=", StringComparison.Ordinal)) { break; }
                        }

                        Console.WriteLine("初始状态：" + state);
                        Console.WriteLine("宿主控件底色：" + pane.ReadPaneBackColor());
                        Console.WriteLine();

                        Assert(
                            state.StartsWith("theme=", StringComparison.Ordinal),
                            "页面加载并读到主题状态",
                            state);

                        var first = Parse(state);
                        var startTheme = first.TryGetValue("theme", out var t0) ? t0 : string.Empty;
                        Assert(
                            startTheme == "light" || startTheme == "dark",
                            "首屏已定出主题（不是未设置）",
                            "theme=" + startTheme);

                        // 切到另一套，逐项确认真的变了。
                        var afterToggle = pane.ClickThemeToggle();
                        await System.Threading.Tasks.Task.Delay(300);
                        var second = Parse(pane.ReadThemeState());
                        Console.WriteLine("切换后：" + pane.ReadThemeState());
                        Console.WriteLine("宿主控件底色：" + pane.ReadPaneBackColor());
                        Console.WriteLine();

                        Assert(
                            afterToggle == (startTheme == "light" ? "dark" : "light"),
                            "点击后主题切到了另一套",
                            "切换后 = " + afterToggle);

                        foreach (var key in new[] { "body", "text", "bar", "composer", "send" })
                        {
                            var before = first.TryGetValue(key, out var b) ? b : "<无>";
                            var after = second.TryGetValue(key, out var a) ? a : "<无>";
                            Assert(before != after, $"{key} 的实际颜色随主题变化", $"{before} → {after}");
                        }

                        // color-scheme 决定原生部件（滚动条、下拉框）跟不跟着变。
                        Assert(
                            first.TryGetValue("scheme", out var s1) && s1 == startTheme,
                            "color-scheme 与主题一致",
                            "scheme=" + s1);

                        // 太阳与月亮同时只能显示一个，否则按钮里会挤两个图标。
                        Assert(
                            first.TryGetValue("glyph", out var g1) && (g1 == "sun" || g1 == "moon"),
                            "只显示一个主题图标",
                            "glyph=" + g1);
                        Assert(
                            second.TryGetValue("glyph", out var g2) && g2 != g1 &&
                                (g2 == "sun" || g2 == "moon"),
                            "切换后换成另一个图标",
                            $"{g1} → {g2}");

                        // 宿主控件的底色必须跟着走，且与页面的 body 底色一致。
                        var paneColor = pane.ReadPaneBackColor();
                        var bodyColor = second.TryGetValue("body", out var bodyRgb) ? bodyRgb : string.Empty;
                        Assert(
                            SameColor(paneColor, bodyColor),
                            "宿主控件底色与页面底色一致",
                            $"控件 {paneColor} 对 页面 {bodyColor}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine("  失败  检查过程抛出异常");
                        Console.WriteLine("        " + ex.Message);
                    }
                    finally
                    {
                        form.Close();
                    }
                };

                Application.Run(form);
            }

            Console.WriteLine();
            Console.WriteLine($"=== 主题实测：{(failed == 0 ? "全部通过" : $"失败 {failed} 项")} ===");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>把 a=1|b=2 拆成字典。</summary>
        private static System.Collections.Generic.Dictionary<string, string> Parse(string state)
        {
            var map = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var part in (state ?? string.Empty).Split('|'))
            {
                var index = part.IndexOf('=');
                if (index > 0)
                {
                    map[part.Substring(0, index)] = part.Substring(index + 1);
                }
            }

            return map;
        }

        /// <summary>比较 "27,29,33" 与 "rgb(27, 29, 33)" 是否同色。</summary>
        private static bool SameColor(string rgbTriple, string cssColor)
        {
            var numbers = System.Text.RegularExpressions.Regex.Matches(cssColor ?? string.Empty, @"\d+");
            if (numbers.Count < 3) { return false; }

            var css = $"{numbers[0].Value},{numbers[1].Value},{numbers[2].Value}";
            return css == (rgbTriple ?? string.Empty).Trim();
        }

        /// <summary>
        /// 把面板渲染成 PNG，供目视确认。
        ///
        /// scripts/capture-panel.ps1 要有 Excel 才能截，本模式不需要宿主，
        /// 因此适合用来逐主题逐页地看配色。两套主题 × 对话/设置两页，
        /// 存成四张图。
        /// </summary>
        private static int RunCapture(string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            var saved = 0;

            using (var form = new Form
            {
                Text = "ChatSheet 截图",
                Width = 420,
                Height = 760,
                StartPosition = FormStartPosition.CenterScreen,
            })
            {
                var pane = new TaskPaneControl { Dock = DockStyle.Fill };
                form.Controls.Add(pane);

                form.Shown += async (s, e) =>
                {
                    try
                    {
                        // 等首屏。
                        for (var i = 0; i < 40; i++)
                        {
                            await System.Threading.Tasks.Task.Delay(500);
                            if (pane.ReadThemeState().StartsWith("theme=", StringComparison.Ordinal))
                            {
                                break;
                            }
                        }

                        for (var round = 0; round < 2; round++)
                        {
                            var theme = Parse(pane.ReadThemeState()).TryGetValue("theme", out var t)
                                ? t
                                : "unknown";

                            foreach (var route in new[] { "chat", "settings" })
                            {
                                pane.NavigateTo(route);
                                // 设置页要等 settings.get 回来并渲染完。
                                await System.Threading.Tasks.Task.Delay(1500);

                                var path = Path.Combine(outputDir, $"{theme}-{route}.png");
                                Capture(form, path);
                                saved++;
                                Console.WriteLine("已保存 " + path);
                            }

                            pane.NavigateTo("chat");
                            await System.Threading.Tasks.Task.Delay(400);
                            pane.ClickThemeToggle();
                            await System.Threading.Tasks.Task.Delay(600);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("截图失败：" + ex);
                    }
                    finally
                    {
                        form.Close();
                    }
                };

                Application.Run(form);
            }

            Console.WriteLine($"共 {saved} 张");
            return saved > 0 ? 0 : 1;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        /// <summary>
        /// 截窗口自身。
        ///
        /// 两条路都试过，只有 PrintWindow 可用：
        ///   · CopyFromScreen 需要可交互的桌面，没有会话时报「句柄无效」；
        ///   · DrawToBitmap 抓不到 WebView2——它是独立的渲染窗口，
        ///     WinForms 的绘制流程里没有它。
        /// PW_RENDERFULLCONTENT(0x2) 是让 DirectComposition 的内容也画进来的关键，
        /// 不带这个标志抓到的是一块空白。
        /// </summary>
        private static void Capture(Form form, string path)
        {
            var bounds = form.Bounds;
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var hdc = graphics.GetHdc();
                    try
                    {
                        if (!PrintWindow(form.Handle, hdc, 0x2))
                        {
                            throw new InvalidOperationException("PrintWindow 失败");
                        }
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }

                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
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
