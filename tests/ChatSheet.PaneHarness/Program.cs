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
