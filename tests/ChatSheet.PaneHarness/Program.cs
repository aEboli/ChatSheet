using System;
using System.Drawing;
using System.IO;
using System.Linq;
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
    ///   ChatSheet.PaneHarness.exe --picker   在真实 WebView2 里验证选择器排版与判定显示
    ///   ChatSheet.PaneHarness.exe --motion   在真实 WebView2 里验证进场动画与点击回弹
    ///   ChatSheet.PaneHarness.exe --shake    用真实鼠标点禁用按钮，验证抖动反馈
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

                if (Array.Exists(
                        args,
                        a => string.Equals(a, "--picker", StringComparison.OrdinalIgnoreCase)))
                {
                    return RunPickerCheck(
                        ParseIntArg(args, "--width", 420),
                        ParseIntArg(args, "--height", 760));
                }

                if (Array.Exists(
                        args,
                        a => string.Equals(a, "--motion", StringComparison.OrdinalIgnoreCase)))
                {
                    return RunMotionCheck();
                }

                if (Array.Exists(
                        args,
                        a => string.Equals(a, "--shake", StringComparison.OrdinalIgnoreCase)))
                {
                    return RunShakeCheck();
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

        /// <summary>
        /// 在真实 WebView2 里验证选择器的排版与判定显示。
        ///
        /// 为什么非要在这里跑：这一屏的几处改动全都只有排版后才成立——
        ///   · 「不可用要一眼看得见」落在算出来的颜色上。假 DOM 没有计算样式，
        ///     CSS 静态检查也看不出 var(--error) 究竟取到了值还是静默退回默认色。
        ///   · 「一行一档」是行高的事。断言 class 在不在证明不了它没折成两行。
        ///   · 「试一下平时藏着」靠 opacity，同理只有计算值算数。
        ///   · 浮层向上弹且 overflow: hidden，高过头的部分被静默裁掉，
        ///     连滚动条都不留——只有量出它的 top 才知道有没有出界。
        ///
        /// 判定用注入的场景，不连真实网关：「不可用」需要服务端点名模型才会得出。
        /// </summary>
        private static int RunPickerCheck(int width, int height)
        {
            var failed = 0;
            Console.WriteLine($"窗口 {width}x{height}");
            Console.WriteLine();

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

            // 宽高可指定：浮层有 min-width 与 max-height，两者都只在极端尺寸下
            // 才暴露问题——常见宽度下量出来永远是「没出界」。
            using (var form = new Form
            {
                Text = "ChatSheet 选择器检查",
                Width = width,
                Height = height,
                StartPosition = FormStartPosition.CenterScreen,
            })
            {
                var pane = new TaskPaneControl { Dock = DockStyle.Fill };
                form.Controls.Add(pane);

                form.Shown += async (s, e) =>
                {
                    try
                    {
                        for (var i = 0; i < 40; i++)
                        {
                            await System.Threading.Tasks.Task.Delay(500);
                            if (pane.ReadThemeState().StartsWith("theme=", StringComparison.Ordinal))
                            {
                                break;
                            }
                        }

                        // 注入三态齐全的场景，再展开浮层。
                        pane.DrivePicker("seed-demo");
                        var seeded = string.Empty;
                        for (var i = 0; i < 20; i++)
                        {
                            await System.Threading.Tasks.Task.Delay(250);
                            seeded = pane.DrivePicker("seed-state");
                            if (seeded != "正在注入…" && seeded != "未注入") { break; }
                        }

                        Console.WriteLine("注入：" + seeded);
                        Assert(seeded == "已注入", "能注入三态齐全的模型列表", seeded);

                        pane.DrivePicker("open");
                        await System.Threading.Tasks.Task.Delay(600);
                        Console.WriteLine();

                        // ---- 判定的颜色 ----
                        // 悬停说明的行数与内容：用户报过「第一行是空白」，
                        // 而空白行在折叠空白的报法里看不见，所以单独报一次。
                        foreach (var m in new[] { "seed-ok", "seed-bad", "seed-unknown" })
                        {
                            var v = pane.DrivePicker("verdict:" + m);
                            Console.WriteLine($"  {m} 悬停：{Field(v, "悬停")}");
                            Assert(
                                !Field(v, "悬停").StartsWith("[2行]<NL>", StringComparison.Ordinal) &&
                                    !Field(v, "悬停").StartsWith("[3行]<NL>", StringComparison.Ordinal),
                                $"{m} 的悬停说明第一行不是空白",
                                Field(v, "悬停"));
                        }
                        Console.WriteLine();

                        var ok = pane.DrivePicker("name-color:seed-ok");
                        var bad = pane.DrivePicker("name-color:seed-bad");
                        var unknown = pane.DrivePicker("name-color:seed-unknown");
                        Console.WriteLine("可用：    " + ok);
                        Console.WriteLine("不可用：  " + bad);
                        Console.WriteLine("未确认：  " + unknown);
                        Console.WriteLine();

                        var badColor = Field(bad, "色");
                        var unknownColor = Field(unknown, "色");

                        Assert(
                            badColor.Length > 0 && badColor != unknownColor,
                            "不可用的模型名与未确认的不是同一个颜色",
                            $"不可用 {badColor} 对 未确认 {unknownColor}");

                        // 红：R 明显大于 G 与 B。断言具体色号会让调色板微调变成失败，
                        // 而「是不是红的」才是这条要守的东西。
                        Assert(IsReddish(badColor), "不可用的模型名是红的", badColor);
                        Assert(!IsReddish(unknownColor), "未确认的模型名不是红的", unknownColor);

                        // 可用不上绿：绿是选中态的颜色。seed-ok 恰好也是当前模型，
                        // 因此它这里应当读到选中态的绿——两者不冲突，选中优先。
                        Assert(
                            Field(bad, "行class").Contains("is-unavailable"),
                            "不可用的行带 is-unavailable",
                            Field(bad, "行class"));

                        // 状态点也要跟着变色，且与名字是两处独立的标记。
                        Assert(
                            IsReddish(Field(bad, "点色")),
                            "不可用的状态点是红的",
                            Field(bad, "点色"));

                        // ---- 批量测试：正在测的那一行有一道扫光 ----
                        //
                        // 静态检查看得到 CSS 与 JS 的文本，看不到「这一行此刻真的被
                        // 标记了、动画真的在跑」。而这两件事各有一条静默失败的路：
                        // 标记取错状态（批量期间没有任何一行是 probing）、动画挂在
                        // ::after 上而查询时漏了 subtree 参数。
                        Console.WriteLine();
                        var beforeSweep = pane.DrivePicker("sweep:seed-unknown");
                        Console.WriteLine("推送前：" + beforeSweep);
                        Assert(
                            Field(beforeSweep, "标记") == "false" && Field(beforeSweep, "动画数") == "0",
                            "没在测的行没有扫光（否则整列都在扫，标记就没有意义）",
                            beforeSweep);

                        pane.DrivePicker("bulk-testing:seed-unknown");
                        await System.Threading.Tasks.Task.Delay(400);

                        var sweep = pane.DrivePicker("sweep:seed-unknown");
                        Console.WriteLine("正在测：" + sweep);

                        Assert(
                            Field(sweep, "标记") == "true",
                            "批量测到的那一行被标记为 is-testing",
                            sweep);
                        Assert(
                            ParseInt(Field(sweep, "动画数")) >= 1,
                            "扫光动画真的在跑（关键帧名与 ::after 都接上了）",
                            sweep);
                        Assert(
                            Field(sweep, "伪元素") == "::after",
                            "动画挂在 ::after 上（行本身不动）",
                            sweep);
                        Assert(
                            Field(sweep, "在跑") == "running",
                            "动画处于运行态而不是暂停",
                            sweep);
                        Assert(
                            Field(sweep, "底色").Contains("gradient"),
                            "那一层是渐变而不是实色（实色是移动的色块，不是一道光）",
                            sweep);
                        Assert(
                            Field(sweep, "裁剪") == "hidden",
                            "行被裁剪，扫光不会溢出到相邻行",
                            sweep);
                        Assert(
                            Field(sweep, "吃点击") == "none",
                            "扫光层不吃点击（否则挡住选中那一行）",
                            sweep);

                        // 别的行不该跟着扫。少了这条，「所有行都扫」也会全绿。
                        var idleRow = pane.DrivePicker("sweep:seed-ok");
                        Console.WriteLine("同时的另一行：" + idleRow);
                        Assert(
                            Field(idleRow, "标记") == "false" && Field(idleRow, "动画数") == "0",
                            "同一时刻只有正在测的那一行在扫",
                            idleRow);

                        // 收尾：明确结束批量，免得影响后面关于三态与排版的断言。
                        pane.DrivePicker("bulk-done");
                        await System.Threading.Tasks.Task.Delay(300);

                        var afterSweep = pane.DrivePicker("sweep:seed-unknown");
                        Console.WriteLine("批量结束后：" + afterSweep);
                        Assert(
                            Field(afterSweep, "标记") == "false" && Field(afterSweep, "动画数") == "0",
                            "批量结束后扫光收掉（否则那一行会一直扫下去）",
                            afterSweep);

                        // ---- 批量确认：探完一个就当场上色 ----
                        //
                        // 这一段守的是一个真缺陷：串行批量确认（models.probe.bulk）原先
                        // 只在探测「之前」推一条不带判定的进度，探完什么都不推。于是整批
                        // 结束前一行都不变色，用户看到的就是「批量探测失败的没变红、
                        // 成功的没标绿」。
                        //
                        // 必须在真实渲染器里验，不能只靠面板单测：单测断的是 class 在不在，
                        // 而 class 在、CSS 规则也在、变量名写错时浏览器会静默退回默认色——
                        // 那时颜色还是黑的，单测照样全绿。这里读的是 getComputedStyle 的
                        // 实际颜色。
                        Console.WriteLine();
                        pane.DrivePicker("bulk-testing:seed-unknown");
                        await System.Threading.Tasks.Task.Delay(300);

                        // 先验判「未确认」不上色，再验判「不可用」上红。
                        //
                        // 顺序不能反：Unknown 不覆盖已有判定（与加载项侧
                        // ModelAvailability.Record 同一条规则），所以一旦这一行先被判成
                        // 不可用，后面再推 Unknown 不会把红退掉——那时这条断言测的
                        // 就不是「Unknown 不上色」，而是「Unknown 不覆盖」了。
                        pane.DrivePicker("bulk-settled:seed-unknown:Unknown");
                        await System.Threading.Tasks.Task.Delay(300);

                        var settledUnknown = pane.DrivePicker("name-color:seed-unknown");
                        Console.WriteLine("探完判未确认：" + settledUnknown);
                        Assert(
                            !Field(settledUnknown, "行class").Contains("is-unavailable") &&
                                !Field(settledUnknown, "行class").Contains("is-available"),
                            "判未确认的行不上色（限流花了钱没拿到答案，不能冒充结论）",
                            Field(settledUnknown, "行class"));
                        Assert(
                            !IsReddish(Field(settledUnknown, "色")),
                            "判未确认的模型名不是红的",
                            Field(settledUnknown, "色"));

                        Console.WriteLine();
                        pane.DrivePicker("bulk-testing:seed-unknown");
                        await System.Threading.Tasks.Task.Delay(200);
                        pane.DrivePicker("bulk-settled:seed-unknown:Unavailable");
                        await System.Threading.Tasks.Task.Delay(300);

                        var settledBad = pane.DrivePicker("name-color:seed-unknown");
                        Console.WriteLine("探完判不可用：" + settledBad);
                        Assert(
                            Field(settledBad, "行class").Contains("is-unavailable"),
                            "探完判不可用的行当场带上 is-unavailable（不等整批结束）",
                            Field(settledBad, "行class"));
                        Assert(
                            IsReddish(Field(settledBad, "色")),
                            "探完判不可用的模型名真的算出红色",
                            Field(settledBad, "色"));
                        Assert(
                            IsReddish(Field(settledBad, "点色")),
                            "状态点跟着变红",
                            Field(settledBad, "点色"));

                        // 上了色就不该再挂扫光：两个互相矛盾的标记压在同一行，
                        // 用户读不出这一行到底测完了没有。
                        var settledSweep = pane.DrivePicker("sweep:seed-unknown");
                        Console.WriteLine("上色后的扫光：" + settledSweep);
                        Assert(
                            Field(settledSweep, "标记") == "false" &&
                                Field(settledSweep, "动画数") == "0",
                            "已经上色的那一行不再挂扫光",
                            settledSweep);

                        // 判定落定之后再推一条 Unknown，红不该退掉——限流不是证据，
                        // 不能把上一次测出来的结论抹成灰。这条与上面那条一起把
                        // 「Unknown 不上色」和「Unknown 不覆盖」区分清楚。
                        pane.DrivePicker("bulk-settled:seed-unknown:Unknown");
                        await System.Threading.Tasks.Task.Delay(300);

                        var afterUnknown = pane.DrivePicker("name-color:seed-unknown");
                        Console.WriteLine("红之后再推未确认：" + afterUnknown);
                        Assert(
                            Field(afterUnknown, "行class").Contains("is-unavailable") &&
                                IsReddish(Field(afterUnknown, "色")),
                            "已经测出不可用的行，再来一条「未确认」不会把红退掉",
                            afterUnknown);

                        // ---- 并发在飞的几行同时扫 ----
                        //
                        // 整份目录那条路并发 5：同一时刻在飞的就是五个模型。早先面板
                        // 只用一个字段记「正在测哪一个」，而那条路只在探完之后推进度，
                        // 于是标的永远是刚探完、已经上了色的那一行，真正在飞的五个
                        // 一个都没标。这里同时推两条 starting，两行都要在扫。
                        Console.WriteLine();
                        pane.DrivePicker("bulk-testing:seed-ok");
                        pane.DrivePicker("bulk-testing:seed-bad");
                        await System.Threading.Tasks.Task.Delay(400);

                        var flyA = pane.DrivePicker("sweep:seed-ok");
                        var flyB = pane.DrivePicker("sweep:seed-bad");
                        Console.WriteLine("同时在飞 A：" + flyA);
                        Console.WriteLine("同时在飞 B：" + flyB);
                        Assert(
                            Field(flyA, "标记") == "true" && Field(flyB, "标记") == "true",
                            "同时在飞的两行都被标成正在测（并发下不是只标一行）",
                            flyA + " || " + flyB);
                        Assert(
                            ParseInt(Field(flyA, "动画数")) >= 1 &&
                                ParseInt(Field(flyB, "动画数")) >= 1,
                            "两行的扫光动画都真的在跑",
                            flyA + " || " + flyB);

                        // 探完一个：它退出在飞，另一个仍在扫。
                        pane.DrivePicker("bulk-settled:seed-ok:Available");
                        await System.Threading.Tasks.Task.Delay(300);

                        var settledA = pane.DrivePicker("sweep:seed-ok");
                        var stillB = pane.DrivePicker("sweep:seed-bad");
                        Console.WriteLine("探完的那行：" + settledA);
                        Console.WriteLine("仍在飞的那行：" + stillB);
                        Assert(
                            Field(settledA, "标记") == "false" && Field(settledA, "动画数") == "0",
                            "探完的那一行退出在飞，扫光收掉",
                            settledA);
                        Assert(
                            Field(stillB, "标记") == "true" &&
                                ParseInt(Field(stillB, "动画数")) >= 1,
                            "同时在飞的另一行不受影响，仍在扫",
                            stillB);

                        pane.DrivePicker("bulk-done");
                        await System.Threading.Tasks.Task.Delay(300);

                        var allQuiet = pane.DrivePicker("sweep:seed-bad");
                        Console.WriteLine("批量置空后：" + allQuiet);
                        Assert(
                            Field(allQuiet, "标记") == "false" && Field(allQuiet, "动画数") == "0",
                            "批量结束后在飞的那几行也一起收掉（它们不会再收到 settled）",
                            allQuiet);

                        // ---- 一行一档 ----
                        Console.WriteLine();
                        foreach (var level in new[] { "Off", "Minimal", "High", "Max" })
                        {
                            var row = pane.DrivePicker("thinking-row:" + level);
                            Console.WriteLine($"{level,-8} {row}");

                            Assert(
                                Field(row, "行内说明") == "无",
                                $"{level} 行上没有说明文字",
                                row);
                            Assert(
                                Field(row, "悬停").Length > 1 && Field(row, "悬停") != "无",
                                $"{level} 的说明在悬停里",
                                row);

                            var height = ParseInt(Field(row, "高"));
                            Assert(height > 0 && height <= 30, $"{level} 只占一行（高 {height}px）", row);
                        }

                        // 注入时 thinkingSupported 只到 High，因此 XHigh 与 Max 应带降级标注。
                        var maxRow = pane.DrivePicker("thinking-row:Max");
                        Assert(
                            Field(maxRow, "降级标注") == "会降级",
                            "不支持的档位在行上留标注（不许收进悬停）",
                            maxRow);
                        var highRow = pane.DrivePicker("thinking-row:High");
                        Assert(
                            Field(highRow, "降级标注") == "无",
                            "支持的档位没有标注",
                            highRow);

                        // 档位列按内容定宽，装得下最长的档位名加降级标注。
                        // 不再要求它拿到整段宽度——那是上下排布时的判据，现在是分栏。
                        //
                        // 档位列宽现在由 max-content 决定，不再是写死的数值，因此这里
                        // 不去核对绝对值——那等于把浏览器量出来的结果再抄一遍常量。
                        // 要守的是两头：装得下最长那一行（不折行，行高已在上面断过），
                        // 且不吃掉浮层一半以上（横向空间归模型 ID）。
                        var rowWidth = ParseInt(Field(highRow, "宽"));
                        var popWidth = ParseInt(Field(pane.DrivePicker("pop-geometry"), "宽"));
                        Assert(
                            rowWidth >= 70,
                            $"档位行宽度够放下档位名与标注（{rowWidth}px / 浮层 {popWidth}px）",
                            highRow);
                        // 反过来守另一头：档位列不该把浮层吃掉一半以上，
                        // 横向空间归模型 ID。
                        Assert(
                            popWidth <= 0 || rowWidth < popWidth * 0.5,
                            $"档位列不超过浮层一半（{rowWidth} / {popWidth}）",
                            highRow);
                        // 浮层本身不许长到占满面板：宽度是定值 300px，
                        // 只在面板比它还窄时才让位。
                        var viewW = ParseInt(Field(pane.DrivePicker("pop-geometry"), "视口宽"));
                        Assert(
                            popWidth <= viewW - 24 + 1,
                            $"浮层不超过面板可用宽度（实测 {popWidth}px，面板 {viewW}px）",
                            "");
                        // 档位行不折行：行高应当只有一行文字的量级。这是 132px 那个
                        // 列宽要守住的东西——宽度算错时标注会折到第二行，行高翻倍。
                        Assert(
                            ParseInt(Field(highRow, "高")) <= 30,
                            $"档位行仍只占一行（高 {ParseInt(Field(highRow, "高"))}px）",
                            highRow);

                        // ---- 对齐 ----
                        //
                        // 档位列的标题与档位名都居中，因此两者的中心必须落在同一处。
                        // 此前差 7px：列表预留了滚动条槽位（那是给模型列的），于是行只占到
                        // 列宽的一部分，行内居中的档位名与列头居中的标题就错开了。
                        // 这类偏差没有任何报错，只能靠量。
                        Console.WriteLine();
                        var align = pane.DrivePicker("align-geometry");
                        Console.WriteLine("对齐：" + align);

                        Func<string, double> centreOf = key =>
                        {
                            var v = Field(align, key);
                            var bits = v.Split(new[] { ".." }, StringSplitOptions.None);
                            if (bits.Length != 2) { return double.NaN; }
                            return (ParseInt(bits[0]) + ParseInt(bits[1])) / 2.0;
                        };

                        var colCentre = centreOf("档位列");
                        var headCentre = centreOf("档位列头字");
                        var nameCentre = centreOf("首个档位名");

                        // 真正要守的是这一条：标题与档位名同心。肉眼说的「没对齐」
                        // 指的就是它们彼此错开，而不是它们与某个几何中心的关系。
                        Assert(
                            !double.IsNaN(headCentre) && !double.IsNaN(nameCentre) &&
                                Math.Abs(headCentre - nameCentre) <= 2,
                            $"标题与档位名同心（相差 {headCentre - nameCentre:0.#}px）",
                            "两者都居中却不同心，说明其中一个的可用宽度被什么占掉了");

                        // 与整列的几何中心允许差半条滚动条槽（3px）：那条槽是列的一部分，
                        // 但不是内容能用的地方，内容只能在余下的空间里居中。
                        // 差得更多就说明有别的东西在占宽。
                        Assert(
                            !double.IsNaN(colCentre) && !double.IsNaN(headCentre) &&
                                Math.Abs(headCentre - colCentre) <= 4,
                            $"标题在内容区居中（与整列中心差 {headCentre - colCentre:0.#}px，" +
                                "允许半条滚动条槽）",
                            align);
                        Assert(
                            !double.IsNaN(nameCentre) &&
                                Math.Abs(nameCentre - colCentre) <= 4,
                            $"档位名在内容区居中（与整列中心差 {nameCentre - colCentre:0.#}px）",
                            align);

                        // 模型列这一侧是靠左的：状态点在最左，名字紧随其后。
                        var dotBox = Field(align, "首个状态点").Split(new[] { ".." }, StringSplitOptions.None);
                        var nameBox = Field(align, "首个模型名").Split(new[] { ".." }, StringSplitOptions.None);
                        if (dotBox.Length == 2 && nameBox.Length == 2)
                        {
                            Assert(
                                ParseInt(dotBox[1]) <= ParseInt(nameBox[0]),
                                $"模型行里状态点在名字之前（点 {Field(align, "首个状态点")}，" +
                                    $"名 {Field(align, "首个模型名")}）",
                                align);
                        }

                        // ---- 列头单行、模型名单行 ----
                        Console.WriteLine();
                        var headGeo = pane.DrivePicker("head-geometry");
                        Console.WriteLine("列头：" + headGeo);
                        // 装得下就必须是单行；装不下时允许折行，但绝不允许溢出——
                        // 溢出会让最右边的按钮整个点不到，且不产生滚动条。
                        var headNeed = Field(headGeo, "需要宽").Split('/');
                        var need = headNeed.Length > 0 ? ParseInt(headNeed[0]) : -1;
                        var avail = headNeed.Length > 1 ? ParseInt(headNeed[1]) : -1;
                        if (need > 0 && avail >= need)
                        {
                            Assert(
                                Field(headGeo, "列头行数") == "1",
                                $"装得下时列头是单行（需要 {need} ≤ 可用 {avail}，" +
                                    $"实测 {Field(headGeo, "列头行数")} 行，" +
                                    $"元素：{Field(headGeo, "元素文字")}）",
                                "折出来的那一行会把列表往下顶");
                        }
                        else
                        {
                            Console.WriteLine(
                                $"        列头需要 {need}px 但只有 {avail}px：" +
                                $"允许折行（实测 {Field(headGeo, "列头行数")} 行），不允许溢出");
                        }
                        // 溢出与折行是两种不同的失败：nowrap 之下装不下不会折行，
                        // 而是横向溢出被裁掉，按钮就点不到了。
                        Assert(
                            Field(headGeo, "列头溢出") == "false",
                            $"列头没有横向溢出（需要/可用 {Field(headGeo, "需要宽")}）",
                            "溢出被裁掉时右端的按钮点不到");
                        // 单行的自然高度约 30px：按钮的行高 17 + 内边距与描边各 2 ≈ 22，
                        // 加列头自身上下内边距 8。折成两行会到 50px 以上。
                        // 「是不是单行」由上面的行数与溢出两条判，这条只兜住量级。
                        // 单行约 30px，折成两行约 52px。上限按「最多两行」定：
                        // 三行说明短名也没起作用，那时该重新想办法而不是继续放宽。
                        Assert(
                            ParseInt(Field(headGeo, "列头高")) <= 56,
                            $"列头最多两行（{Field(headGeo, "列头高")}px）",
                            headGeo);
                        // 模型名各占一行：折行时高度会是 32px 上下（两行 × 16px）。
                        var nameHeights = Field(headGeo, "名字高")
                            .Split(',')
                            .Select(x => ParseInt(x))
                            .Where(x => x > 0)
                            .ToList();
                        Assert(
                            nameHeights.Count > 0 && nameHeights.All(h => h <= 20),
                            $"每个模型名都只占一行（高 {string.Join("/", nameHeights)}px）",
                            headGeo);

                        // 注入的目录里有一个四十来字符的真实长度 ID。浮层宽度按内容取，
                        // 因此在够宽的面板上它应当完整显示，不该有任何一行被截断。
                        // 这一条是「模型 ID 一行完整显示」的判据——短名试不出截断。
                        //
                        // 阈值 480px 是量出来的：注入的那个 41 字符 ID 名字本身要 253px，
                        // 加状态点、星标、行与列表的内边距、滚动条槽位之后，面板需约 475px。
                        // 实测 470px 差 4px、480px 恰好装下。窄于此长 ID 仍会截断，
                        // 完整值在悬停第一行——那是取舍，不是缺陷。
                        var panelW = ParseInt(Field(pane.DrivePicker("pop-geometry"), "视口宽"));
                        if (panelW >= 480)
                        {
                            Assert(
                                Field(headGeo, "被截断") == "0",
                                $"够宽的面板上长 ID 完整显示（面板 {panelW}px，" +
                                    $"被截断 {Field(headGeo, "被截断")} 个，最宽名 {Field(headGeo, "最宽名")}px）",
                                headGeo);
                        }
                        else
                        {
                            Console.WriteLine(
                                $"        面板仅 {panelW}px：长 ID 截断 {Field(headGeo, "被截断")} 个，" +
                                $"还差 {Field(headGeo, "还差")}px，完整值在悬停里");
                        }

                        // 把 seed-unknown 复原成「未确认」。
                        //
                        // 上面那几节把它一路探成了不可用，而下面「试一下」那一节要的
                        // 正是一个没有判定的行——有结论的行不挂「试一下」。重新注入
                        // 一次即可：seed-demo 的 availability 里本就没有 seed-unknown，
                        // 而 adoptFavorites 是整份替换，不是合并。
                        pane.DrivePicker("seed-demo");
                        await System.Threading.Tasks.Task.Delay(600);

                        // ---- 「试一下」平时藏着 ----
                        Console.WriteLine();
                        var vis = pane.DrivePicker("probe-visible:seed-unknown");
                        Console.WriteLine("「试一下」：" + vis);
                        Assert(vis.Contains("在DOM=true"), "「试一下」在 DOM 里", vis);

                        // 真实鼠标可能恰好停在这一行上（窗口居中弹出时常有），那时它
                        // 按设计就是显形的。据此分两种断言，而不是让结果随鼠标位置漂。
                        var hovered = Field(vis, "被悬停")
                            .Equals("true", StringComparison.OrdinalIgnoreCase);
                        if (hovered)
                        {
                            Console.WriteLine("        鼠标正停在这一行上，改为断言「显形」");
                            Assert(Field(vis, "透明度") == "1", "被悬停时透明度为 1", vis);
                            Assert(
                                Field(vis, "可点").Equals("true", StringComparison.OrdinalIgnoreCase),
                                "被悬停时可点",
                                vis);
                        }
                        else
                        {
                            Assert(Field(vis, "透明度") == "0", "不悬停时透明度为 0", vis);
                            Assert(
                                Field(vis, "可点").Equals("false", StringComparison.OrdinalIgnoreCase),
                                "不悬停时不接收点击",
                                vis);
                        }

                        // ---- 浮层没有出界 ----
                        Console.WriteLine();
                        var geo = pane.DrivePicker("pop-geometry");
                        Console.WriteLine("浮层：" + geo);
                        Assert(
                            Field(geo, "出界") == "false",
                            "浮层没有超出视口顶端（超出的部分会被静默裁掉）",
                            geo);

                        var popHeight = ParseInt(Field(geo, "高"));
                        var viewHeight = ParseInt(Field(geo, "视口高"));
                        Assert(
                            popHeight > 0 && viewHeight > 0 && popHeight <= viewHeight,
                            $"浮层高度不超过视口（{popHeight} / {viewHeight}）",
                            geo);
                        // 高度够时七档应当全列出来（约 190px）；不够时它必须肯让，
                        // 而让出来的高度要落在模型段的下限之外——两段都被压到看不见
                        // 才是缺陷，一段让另一段是设计。
                        var thinkingHeight = ParseInt(Field(geo, "档位段高"));
                        var modelHeight = ParseInt(Field(geo, "模型段高"));
                        if (viewHeight >= 520)
                        {
                            Assert(
                                thinkingHeight >= 150,
                                $"高度够时七档完整展开（{thinkingHeight}px）",
                                geo);
                        }
                        else
                        {
                            Console.WriteLine(
                                $"        面板仅 {viewHeight}px：档位段让到 {thinkingHeight}px，" +
                                $"模型段守住 {modelHeight}px");
                            Assert(
                                thinkingHeight > 0,
                                $"矮面板下档位段仍有高度（{thinkingHeight}px）",
                                geo);
                        }

                        // 无论高矮，模型段都不该被压到低于下限——那等于「浮层开了
                        // 但模型列表空着」，而它是这个控件的主体。
                        Assert(
                            modelHeight >= 78,
                            $"模型段守住高度下限（{modelHeight}px）",
                            geo);
                        // 只有真的装不下时才要求出滚动条。注入的目录只有三个模型，
                        // 矮视口下 80px 的下限仍装得下它们——此时不滚是对的，
                        // 无条件要求会把「没东西可滚」当成缺陷。
                        Assert(
                            viewHeight >= 520 ||
                                modelHeight > 80 ||
                                Field(geo, "模型段可滑") == "true",
                            "被压到下限且装不下时模型段可滚动（否则拿不到剩下的模型）",
                            geo);
                        Assert(
                            Field(geo, "右出界") == "false",
                            "浮层没有超出面板右缘（写 width 之后 max-width 才管得住它）",
                            geo);
                        // 宽度是定值 300px：够宽的面板上应当恰好是它，窄面板上让位给
                        // max-width。两头都要守——长到占满整行和被裁掉一样是缺陷。
                        // 浮层宽度按内容取（max-content），上限是面板宽度。因此不核对
                        // 某个定值，只守两头：不超过面板可用宽度，且不为 0。
                        var viewWidth = ParseInt(Field(geo, "视口宽"));
                        var popW = ParseInt(Field(geo, "宽"));
                        var usable = viewWidth - 24;
                        Assert(
                            popW > 0 && popW <= usable + 1,
                            $"浮层不超过面板可用宽度（{popW}px ≤ {usable}px，面板 {viewWidth}px）",
                            geo);

                        // ---- 另一套主题下同样成立 ----
                        //
                        // 起始主题取决于上次存了什么，因此这里读出来再报，不写死
                        // 「先浅后深」——两套的顺序反过来时断言仍然成立，但日志会
                        // 说反，而那种日志比没有更坏。
                        Console.WriteLine();
                        var firstTheme = Parse(pane.ReadThemeState()).TryGetValue("theme", out var ft)
                            ? ft
                            : "unknown";

                        pane.ClickThemeToggle();
                        await System.Threading.Tasks.Task.Delay(500);

                        // 主题按钮在浮层外面，点它会走「点浮层外部即关闭」那条路，
                        // 浮层因此已经收起——这是对的行为，重新展开再量。
                        pane.DrivePicker("open");
                        await System.Threading.Tasks.Task.Delay(500);

                        var secondTheme = Parse(pane.ReadThemeState()).TryGetValue("theme", out var st)
                            ? st
                            : "unknown";
                        var otherBad = pane.DrivePicker("name-color:seed-bad");
                        Console.WriteLine($"{secondTheme} 下的不可用：{otherBad}");

                        var otherColor = Field(otherBad, "色");
                        Assert(
                            secondTheme != firstTheme && secondTheme != "unknown",
                            $"主题确实切换了（{firstTheme} → {secondTheme}）",
                            secondTheme);
                        Assert(IsReddish(otherColor), $"{secondTheme} 主题下不可用仍是红的", otherColor);
                        Assert(
                            otherColor != badColor,
                            "两套主题各用自己那一份红（不是照搬另一套）",
                            $"{firstTheme} {badColor} 对 {secondTheme} {otherColor}");

                        var otherGeo = pane.DrivePicker("pop-geometry");
                        Assert(
                            Field(otherGeo, "出界") == "false",
                            $"{secondTheme} 下浮层同样没有出界",
                            otherGeo);

                        // 扫光在另一套主题下也要成立，且用的是那一套自己的高光色：
                        // 浅色下压暗、深色下提亮，照搬另一套等于其中一套看不见。
                        pane.DrivePicker("bulk-testing:seed-unknown");
                        await System.Threading.Tasks.Task.Delay(400);
                        var otherSweep = pane.DrivePicker("sweep:seed-unknown");
                        Console.WriteLine($"{secondTheme} 下的扫光：" + otherSweep);
                        Assert(
                            Field(otherSweep, "标记") == "true" &&
                                ParseInt(Field(otherSweep, "动画数")) >= 1,
                            $"{secondTheme} 主题下扫光同样在跑",
                            otherSweep);
                        Assert(
                            Field(otherSweep, "底色") != Field(sweep, "底色"),
                            "两套主题各用自己那一份高光色（不是照搬另一套）",
                            $"{firstTheme} {Field(sweep, "底色")} 对 {secondTheme} {Field(otherSweep, "底色")}");
                        pane.DrivePicker("bulk-done");
                        await System.Threading.Tasks.Task.Delay(250);

                        // 档位行在另一套主题下也仍是一行。深色下字重与行高都可能不同。
                        var otherRow = pane.DrivePicker("thinking-row:Max");
                        Assert(
                            ParseInt(Field(otherRow, "高")) <= 30,
                            $"{secondTheme} 下档位仍只占一行",
                            otherRow);
                        Assert(
                            Field(otherRow, "降级标注") == "会降级",
                            $"{secondTheme} 下降级标注仍在行上",
                            otherRow);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine("  失败  检查过程抛出异常");
                        Console.WriteLine("        " + ex);
                    }
                    finally
                    {
                        form.Close();
                    }
                };

                Application.Run(form);
            }

            Console.WriteLine();
            Console.WriteLine($"=== 选择器实测：{(failed == 0 ? "全部通过" : $"失败 {failed} 项")} ===");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// 在真实 WebView2 里验证进场动画与顶栏图标的点击回弹。
        ///
        /// 为什么非要在这里跑：这两处的正确性全落在「动画此刻是否在跑」上，
        /// 而那个状态只有真实渲染器有。Node 侧的静态检查看得到 CSS 与 JS 的文本，
        /// 看不到 append 一个已在场的节点会把运行中的动画取消并重播——
        /// 那是 DOM 规范的行为，代码里没有任何痕迹，表现只是气泡闪两下。
        ///
        /// 三件事按重要性排：重挂是否重播（会造成可见的双闪）、动画被取消时类
        /// 是否摘得掉（残留会在日后补放一次）、连点是否重新起播（点击反馈的全部意义）。
        /// </summary>
        private static int RunMotionCheck()
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
                Text = "ChatSheet 动效检查",
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
                        for (var i = 0; i < 40; i++)
                        {
                            await System.Threading.Tasks.Task.Delay(500);
                            if (pane.ReadThemeState().StartsWith("theme=", StringComparison.Ordinal))
                            {
                                break;
                            }
                        }

                        // ---- 首次挂载会放进场动画 ----
                        pane.DriveMotion("reset");
                        var mounted = pane.DriveMotion("mount");
                        Console.WriteLine("首挂：" + mounted);
                        Assert(
                            mounted.Contains("is-entering"),
                            "首次挂载挂上了进场类",
                            mounted);
                        Assert(
                            Field(mounted, "动画").StartsWith("transcript-enter", StringComparison.Ordinal),
                            "进场动画真的在跑（CSS 里的关键帧名与类都接上了）",
                            mounted);

                        // ---- 重挂：动画不该从头重播 ----
                        //
                        // 这是这一模式存在的首要理由。append 一个已是子节点的元素
                        // 等于「先摘再插」，而移出文档会取消动画——重播的表现是
                        // 气泡可见地闪两下，而代码里看不出任何问题。
                        //
                        // 量「进度有没有退回去」必须让渲染器出帧：同一个 JS 任务内
                        // document.timeline.currentTime 是常量，在页面里忙等推不动它。
                        // 所以这里等一段再发第二次调用，进程内的往返足够快，
                        // 实测重挂时动画仍在 130-170ms 处。
                        //
                        // 经 COM 驱动真实 Excel 时往返超过 0.18s，那侧改断一条与
                        // 时序无关的不变式（见 scripts/verify-motion-host.ps1）。
                        await System.Threading.Tasks.Task.Delay(90);
                        var remounted = pane.DriveMotion("remount");
                        Console.WriteLine("重挂：" + remounted);

                        var before = ParseAnimTime(Field(remounted, "重挂前"));
                        var after = ParseAnimTime(Field(remounted, "重挂后"));
                        Console.WriteLine($"        重挂前 {before}ms → 重挂后 {after}ms");

                        // 这条测量本身有竞态：动画只有 0.18s，等待加往返有时会落到
                        // 窗口外，那时动画已自然放完、量到的是「无」。竞态的断言比
                        // 没有更坏，所以只在真的量到在跑的动画时才断言，否则明说跳过
                        // ——硬判据是下面那条与时序无关的同任务不变式。
                        if (before > 0)
                        {
                            // 判据是「有没有退回去」：继续往前或已经结束都算对，
                            // 退回接近 0 就是重播。断言相对关系而不是具体毫秒数——
                            // 时长改一次不该让这条失败。
                            Assert(
                                after < 0 || after >= before,
                                $"重挂没有把进场动画倒回重播（{before}ms → {after}ms）",
                                "退回接近 0 说明动画被取消并重启，用户看到的是闪两下");
                        }
                        else
                        {
                            Console.WriteLine(
                                "  跳过  没赶上动画窗口（0.18s），进度这条本次量不到；" +
                                "下面的同任务不变式与时序无关，照样能判");
                        }

                        // 同一条事实的另一种判据：把首挂与重挂放进同一个 JS 任务，
                        // 中间不出帧，animationend 绝无可能已触发——类此刻在不在
                        // 完全取决于代码有没有主动摘。与时序无关，因此两个版本必然
                        // 给出不同结果。上面那条量的是进度，这条量的是类的去留。
                        var sameTask = pane.DriveMotion("remount-same-task");
                        Console.WriteLine("同任务重挂：" + sameTask);
                        Assert(
                            Field(sameTask, "首挂").Contains("is-entering") &&
                                Field(sameTask, "首挂").Contains("transcript-enter@"),
                            "同任务里首挂确实加了类并起播（下一条断言的前提）",
                            sameTask);
                        Assert(
                            !Field(sameTask, "重挂后类").Contains("is-entering"),
                            "同任务重挂后不再带进场类",
                            sameTask);
                        Assert(
                            Field(sameTask, "重挂后动画") == "无",
                            "同任务重挂后没有动画在跑（带类重挂会被 append 重播）",
                            sameTask);

                        // ---- 动画被取消时类要摘得掉 ----
                        //
                        // 搬进未渲染的容器（sealOpsBatch 把卡片搬进 details 的 body）
                        // 会触发 animationcancel 而不是 animationend。只听后者的话
                        // 类永久残留，日后节点被重插时会再淡入一次。
                        //
                        // 用工具卡片而不是指示器气泡：每次推送都新建一张卡，因此
                        // 拿到的一定是全新的首挂、动画确实在跑。指示器气泡不行——
                        // 清 DOM 清不掉 chat.js 里的 pendingBubble 引用，之后的推送
                        // 会走「气泡已存在」的重挂分支，那时没有动画可取消，
                        // 这一条就变成了假绿。
                        Console.WriteLine();
                        var card = pane.DriveMotion("card");
                        Console.WriteLine("新卡：" + card);
                        Assert(
                            Field(card, "动画").StartsWith("transcript-enter", StringComparison.Ordinal),
                            "工具卡片首挂时进场动画在跑（下一条断言的前提）",
                            card);

                        var movedCard = pane.DriveMotion("move-card-away");
                        Console.WriteLine("搬走：" + movedCard);
                        // 搬走前动画必须还在跑，否则「取消」无从发生，
                        // 后面那条断言会因为压根没有动画而轻松通过。
                        Assert(
                            ParseAnimTime(Field(movedCard, "搬前")) >= 0,
                            "搬走时动画确实还在跑（否则下一条断言测不到取消）",
                            movedCard);

                        await System.Threading.Tasks.Task.Delay(150);
                        var settled = pane.DriveMotion("card-state");
                        Console.WriteLine("搬走后：" + settled);
                        Assert(
                            Field(settled, "残留") == "false",
                            "动画被取消后进场类摘掉了（只听 animationend 会永久残留）",
                            settled);

                        // ---- 动画放完类要摘掉 ----
                        var card2 = pane.DriveMotion("card");
                        Console.WriteLine("新卡：" + card2);
                        Assert(
                            Field(card2, "动画").StartsWith("transcript-enter", StringComparison.Ordinal),
                            "第二张卡同样从首挂开始放动画",
                            card2);

                        await System.Threading.Tasks.Task.Delay(500);
                        var finished = pane.DriveMotion("card-state");
                        Console.WriteLine("放完后：" + finished);
                        Assert(
                            Field(finished, "残留") == "false",
                            "动画正常放完后进场类摘掉了",
                            finished);
                        Assert(
                            Field(finished, "动画") == "无",
                            "放完后已经没有动画在跑",
                            finished);

                        pane.DriveMotion("reset");

                        // ---- 顶栏图标的点击回弹 ----
                        Console.WriteLine();
                        foreach (var id in new[] { "chat", "settings", "theme" })
                        {
                            var tapped = pane.DriveMotion("tap:" + id);
                            Console.WriteLine($"点 {id,-8}：{tapped}");

                            Assert(
                                Field(tapped, "绑定") == "true",
                                $"{id} 按钮在 .app-nav .nav-btn 的选择范围内（否则绑定静默漏掉它）",
                                tapped);
                            Assert(
                                tapped.Contains("is-tapped"),
                                $"点 {id} 之后挂上了回弹类",
                                tapped);

                            // 页签用 nav-tap，主题切换用 theme-tap（ID 选择器压过类规则）。
                            var expected = id == "theme" ? "theme-tap" : "nav-tap";
                            Assert(
                                Field(tapped, "动画").StartsWith(expected, StringComparison.Ordinal),
                                $"{id} 放的是 {expected}（关键帧名与选择器优先级都对）",
                                tapped);

                            await System.Threading.Tasks.Task.Delay(420);
                        }

                        // ---- 连点要重新起播 ----
                        //
                        // 对已带同名类的元素再 add 不会重启动画。第二下若读到的
                        // currentTime 比第一下还大，说明它只是同一次动画在继续跑——
                        // 用户连点第二下得不到任何反馈。
                        Console.WriteLine();
                        var twice = pane.DriveMotion("tap-twice:chat");
                        Console.WriteLine("连点：" + twice);
                        var t1 = ParseAnimTime(Field(twice, "第一下"));
                        var t2 = ParseAnimTime(Field(twice, "第二下"));
                        Assert(
                            t1 >= 0 && t2 >= 0 && t2 <= t1 + 5,
                            $"连点第二下重新起播（{t1}ms → {t2}ms）",
                            "第二下的进度不该比第一下更靠后——那说明动画没重启，连点没有反馈");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine("  失败  检查过程抛出异常");
                        Console.WriteLine("        " + ex);
                    }
                    finally
                    {
                        form.Close();
                    }
                };

                Application.Run(form);
            }

            Console.WriteLine();
            Console.WriteLine($"=== 动效实测：{(failed == 0 ? "全部通过" : $"失败 {failed} 项")} ===");
            return failed == 0 ? 0 : 1;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        /// <summary>在屏幕坐标处真实点一下左键。</summary>
        private static void RealClick(int screenX, int screenY)
        {
            SetCursorPos(screenX, screenY);
            System.Threading.Thread.Sleep(60);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        }

        /// <summary>
        /// 用真实鼠标点禁用按钮，验证「点不动就抖一下」。
        ///
        /// 为什么非要真实鼠标：这套反馈的地基是两条浏览器行为——
        ///   · 禁用的按钮不派发点击事件（所以监听装在文档上，不在按钮上）；
        ///   · 但指针命中测试照常命中它（所以能靠 elementFromPoint 判断点在了哪）。
        /// dispatchEvent 造的事件不走命中测试，怎么造都能通过，那样测的是我
        /// 自己的假设而不是浏览器的行为。只有真实指针输入能证实这两条。
        ///
        /// 点的是选择器里的「全部确认」：它在 index.html 里就带 disabled，
        /// 是面板中唯一一个不需要造任何状态就处于禁用态的按钮。
        /// </summary>
        private static int RunShakeCheck()
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

            // 不声明 DPI 感知时，缩放不是 100% 的机器上 SetCursorPos 的坐标会被
            // 系统再缩放一次，点偏到别处去——而断言只会报「没抖」，与真实原因无关。
            try { SetProcessDPIAware(); } catch { }

            using (var form = new Form
            {
                Text = "ChatSheet 抖动检查",
                Width = 460,
                Height = 800,
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = true,
            })
            {
                var pane = new TaskPaneControl { Dock = DockStyle.Fill };
                form.Controls.Add(pane);

                // 看门狗。这一模式会真的动鼠标，点到意料之外的东西就可能永远等下去
                // （第一次跑就点到了「测试」，那会对整份目录逐个发请求）。
                // 到时强制收摊并记为失败，检查绝不允许挂住整条验证链。
                var watchdog = new Timer { Interval = 90000 };
                watchdog.Tick += (ws, we) =>
                {
                    watchdog.Stop();
                    failed++;
                    Console.WriteLine("  失败  超时 90s，强制结束（点到了意料之外的东西？）");
                    form.Close();
                };
                form.Shown += (ws, we) => watchdog.Start();
                form.FormClosed += (ws, we) => watchdog.Dispose();

                form.Shown += async (s, e) =>
                {
                    try
                    {
                        for (var i = 0; i < 40; i++)
                        {
                            await System.Threading.Tasks.Task.Delay(500);
                            if (pane.ReadThemeState().StartsWith("theme=", StringComparison.Ordinal))
                            {
                                break;
                            }
                        }

                        // ---- 一、在真实的产品按钮上验一遍 ----
                        //
                        // 「全部确认」在选择器浮层里，展开后未拉到模型时是禁用的。
                        // 它会随模型列表到达而变成可点，所以这一组是「碰上了就验」：
                        // 禁用态是它的前提，等到不禁用了就明说跳过，不硬断言——
                        // 机制本身在下面的注入按钮上有一份不受异步影响的硬断言。
                        pane.DrivePicker("open");
                        await System.Threading.Tasks.Task.Delay(600);

                        const string target = "#picker-probe-all";
                        var info = pane.DriveMotion("disabled-at:" + target);
                        Console.WriteLine("产品按钮：" + info);

                        var coords = Field(info, "视口坐标").Split(',');
                        var productDisabled = Field(info, "禁用") == "true" && coords.Length == 2;

                        if (productDisabled)
                        {
                            // 这一条是整套方案的地基之一：事件不派发，但命中测试照常
                            // 命中禁用按钮，否则文档级监听无从判断点在了哪。
                            Assert(
                                Field(info, "命中它") == "true",
                                "命中测试能拿到禁用的产品按钮本身（elementFromPoint 这条路成立）",
                                info);
                        }
                        else
                        {
                            Console.WriteLine(
                                "        「全部确认」此刻不是禁用态（模型列表已到），" +
                                "这一组跳过；机制由下面的注入按钮硬验");
                        }

                        // 视口坐标 → 屏幕坐标。
                        //
                        // 必须乘 devicePixelRatio。页面报的是 CSS 像素，而声明了
                        // DPI 感知之后 PointToScreen 与 SetCursorPos 用的是物理像素。
                        // 这台机器缩放 150%，直接相加会点偏三分之一——第一次跑就
                        // 踩了：点落在浮层外面，把浮层点关了，然后断言报「没抖」，
                        // 与真实原因（坐标算错）毫无关系。
                        var dpr = double.TryParse(
                            Field(info, "缩放"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsedDpr) && parsedDpr > 0 ? parsedDpr : 1.0;

                        var client = pane.PointToScreen(new Point(0, 0));

                        if (productDisabled)
                        {
                            var sx = client.X + (int)Math.Round(ParseInt(coords[0]) * dpr);
                            var sy = client.Y + (int)Math.Round(ParseInt(coords[1]) * dpr);
                            Console.WriteLine(
                                $"        视口 {coords[0]},{coords[1]} CSS 像素 × 缩放 {dpr} " +
                                $"+ 客户区原点 {client.X},{client.Y} → 屏幕 {sx},{sy}");

                            pane.DriveMotion("watch-refusal");
                            RealClick(sx, sy);
                            // 抖动 0.19s，留足时间让它开始并结束。
                            await System.Threading.Tasks.Task.Delay(500);

                            var log = pane.DriveMotion("refusals");
                            Console.WriteLine("产品按钮记录：" + log);

                            // 这一组本质上有竞态：它要求浮层开着、而模型列表还没到
                            // （列表一到「全部确认」就不再禁用）。列表在量坐标与点击
                            // 之间到达时，浮层内容位移，同一坐标下换成了别的元素——
                            // 实测点到过隔壁模型行上的「试一下」。
                            //
                            // 所以先看这一下究竟点在了谁身上：不是目标就明说跳过，
                            // 不报「没抖」。机制本身在下面的注入按钮上有一份不受
                            // 异步影响的硬断言，这一组只是「碰上了就多验一次产品按钮」。
                            var landedOnTarget = log.Contains(":picker-probe-all:");

                            if (!landedOnTarget)
                            {
                                Console.WriteLine(
                                    "        这一下没落在「全部确认」上（模型列表到达后浮层内容位移），" +
                                    "这一组跳过");
                            }
                            else
                            {
                                Assert(
                                    log.Contains("开始:"),
                                    "真实点击禁用的产品按钮后抖动起播了",
                                    "禁用按钮不派发点击事件，这一条证明文档级监听 + 命中测试这条路真的通");
                            }
                        }

                        // ---- 连点两下，两次都要抖 ----
                        //
                        // 换成位置固定的注入按钮。用浮层里那个产品按钮做这一条会栽在
                        // 异步上：模型列表一到，浮层内容位移，同一坐标下换成了别的
                        // 元素——第一次跑就点到了隔壁的「试一下」，还真起了一次探测。
                        // 机制已由上面的产品按钮验过，这里验的是重放规则。
                        var dis = pane.DriveMotion("add-disabled-button");
                        Console.WriteLine("连点目标（注入的禁用按钮）：" + dis);

                        var dcoords = Field(dis, "视口坐标").Split(',');
                        if (Field(dis, "禁用") != "true" || Field(dis, "命中它") != "true" ||
                            dcoords.Length != 2)
                        {
                            failed++;
                            Console.WriteLine("  失败  造不出可命中的禁用按钮：" + dis);
                        }
                        else
                        {
                            var dx = client.X + (int)Math.Round(ParseInt(dcoords[0]) * dpr);
                            var dy = client.Y + (int)Math.Round(ParseInt(dcoords[1]) * dpr);

                            // 先单点一次，把机制的三条硬断言做完（起播、放完、摘类）。
                            // 这一组不受浮层异步的影响，是机制的权威判据。
                            pane.DriveMotion("watch-refusal");
                            RealClick(dx, dy);
                            await System.Threading.Tasks.Task.Delay(500);

                            var one = pane.DriveMotion("refusals");
                            Console.WriteLine("单点记录：" + one);
                            Assert(
                                one.Contains("按下@"),
                                "真实点击到达了页面（点偏时这一条会红，与「没抖」区分开）",
                                one);
                            Assert(
                                one.Contains("开始:"),
                                "真实点击禁用按钮后抖动起播了（禁用按钮不派发点击事件，" +
                                    "这一条证明文档级监听 + 命中测试这条路真的通）",
                                one);
                            Assert(
                                one.Contains("结束:"),
                                "抖动放完了（不是卡在中途）",
                                one);
                            Assert(
                                one.Contains("残留=false"),
                                "放完后抖动类已摘掉（否则下一次点击不会重新起播）",
                                one);

                            // 再连点两下。第二下必须落在动画还在跑的时候——
                            // 那才是「对已带同名类的元素再 add 不重启」会显形的时刻。
                            pane.DriveMotion("watch-refusal");
                            RealClick(dx, dy);
                            await System.Threading.Tasks.Task.Delay(60);
                            RealClick(dx, dy);
                            await System.Threading.Tasks.Task.Delay(500);

                            var twice = pane.DriveMotion("refusals");
                            Console.WriteLine("连点记录：" + twice);
                            var downs = twice.Split(new[] { "按下@" }, StringSplitOptions.None).Length - 1;
                            var starts = twice.Split(new[] { "开始:" }, StringSplitOptions.None).Length - 1;

                            // 前置断言：两下都得真的点到，否则「只抖一次」说的是点偏了。
                            Assert(
                                downs >= 2,
                                $"两下都点到了（到达文档的 pointerdown {downs} 次）",
                                twice);
                            Assert(
                                starts >= 2,
                                $"连点两下抖了两次（实测起播 {starts} 次）",
                                "重放时摘类会让运行中的动画被取消，而 animationcancel 是异步派发的——" +
                                    "清理处理器若不先确认「此刻没有动画在跑」，会把重放刚加上的类又摘掉");
                        }

                        // ---- 能点的按钮不该抖 ----
                        //
                        // 少了这一条，「所有按钮都抖」也会全绿——而那是个明显的缺陷：
                        // 正常按钮点一下就该干活，抖动等于说它拒绝了。
                        // ---- 对照：可点的按钮不该抖 ----
                        //
                        // 少了这一条，「所有按钮都抖」也会全绿——而那是个明显的缺陷：
                        // 正常按钮点一下就该干活，抖动等于说它拒绝了。
                        //
                        // 用注入的空按钮，不点面板里现成的：现成的可点按钮点下去都会
                        // 真的干活（「测试」会对整份目录逐个发请求，第一次跑就这么把
                        // 检查挂住了；「新会话」会清掉会话）。对照要的只是「一个不
                        // 禁用的按钮」。
                        var enabledInfo = pane.DriveMotion("add-control-button");
                        Console.WriteLine("对照（注入的空按钮）：" + enabledInfo);

                        var ecoords = Field(enabledInfo, "视口坐标").Split(',');
                        if (Field(enabledInfo, "禁用") == "false" && ecoords.Length == 2)
                        {
                            pane.DriveMotion("watch-refusal");
                            RealClick(
                                client.X + (int)Math.Round(ParseInt(ecoords[0]) * dpr),
                                client.Y + (int)Math.Round(ParseInt(ecoords[1]) * dpr));
                            await System.Threading.Tasks.Task.Delay(400);
                            var none = pane.DriveMotion("refusals");
                            Console.WriteLine("对照记录：" + none);
                            Assert(
                                !none.Contains("开始:"),
                                "点可用的按钮不抖（抖动只表示拒绝）",
                                none);
                        }
                        else
                        {
                            failed++;
                            Console.WriteLine("  失败  造不出对照按钮：" + enabledInfo);
                        }

                        pane.DriveMotion("remove-control-button");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine("  失败  检查过程抛出异常");
                        Console.WriteLine("        " + ex);
                    }
                    finally
                    {
                        watchdog.Stop();
                        form.Close();
                    }
                };

                Application.Run(form);
            }

            Console.WriteLine();
            Console.WriteLine($"=== 抖动实测：{(failed == 0 ? "全部通过" : $"失败 {failed} 项")} ===");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// 从 `名字@毫秒` 或 `名字@毫秒+名字@毫秒` 里取第一个毫秒数。
        /// 「无」以及取不到时返回 -1。
        /// </summary>
        private static int ParseAnimTime(string value)
        {
            var text = (value ?? string.Empty).Trim();
            var at = text.IndexOf('@');
            if (at < 0) { return -1; }

            var rest = text.Substring(at + 1);
            var plus = rest.IndexOf('+');
            if (plus >= 0) { rest = rest.Substring(0, plus); }

            return int.TryParse(rest.Trim(), out var parsed) ? parsed : -1;
        }

        /// <summary>从 `a=1 | b=2` 里取一个字段。取不到返回空串。</summary>
        private static string Field(string text, string name)
        {
            foreach (var part in (text ?? string.Empty).Split('|'))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith(name + "=", StringComparison.Ordinal))
                {
                    return trimmed.Substring(name.Length + 1).Trim();
                }
            }

            return string.Empty;
        }

        private static int ParseInt(string value)
        {
            return int.TryParse((value ?? string.Empty).Trim(), out var parsed) ? parsed : -1;
        }

        /// <summary>
        /// 判断一个 rgb() 是不是偏红。
        ///
        /// 不断言具体色号：调色板微调一次就会让断言失败，而要守的是「它是红的」。
        /// 两套主题的红分别是 #b42318 与 #f28b82，都满足 R 比 G、B 高出一截。
        /// </summary>
        private static bool IsReddish(string cssColor)
        {
            var numbers = System.Text.RegularExpressions.Regex.Matches(cssColor ?? string.Empty, @"\d+");
            if (numbers.Count < 3) { return false; }

            var r = int.Parse(numbers[0].Value);
            var g = int.Parse(numbers[1].Value);
            var b = int.Parse(numbers[2].Value);
            return r > g + 40 && r > b + 40;
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

        /// <summary>取 --名字 后面的整数。给不出有效值时用默认值。</summary>
        private static int ParseIntArg(string[] args, string name, int fallback)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], out var parsed) && parsed > 0)
                {
                    return parsed;
                }
            }

            return fallback;
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
