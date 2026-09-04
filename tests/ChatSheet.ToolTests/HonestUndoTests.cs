using System;
using ChatSheet.AddIn.Agent;
using ChatSheet.AddIn.Hosts;
using ChatSheet.AddIn.Tools;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 撤销不许说谎。
    ///
    /// 面板「适配」早就立过规矩：做不到的按钮不要放，宁可缺一个入口并说明原因。
    /// 模型路径此前没守，三处都是「按钮亮着、点下去必然失败或什么也不还原」：
    ///   一、建图不回报 Shape 名，撤销拿 null 去 Shapes.Item，恒定报找不到图表；
    ///   二、外观属性逐项不一致时快照全为 null，还原全部跳过，操作却标成已撤销；
    ///   三、范围相交的乱序撤销会静默盖掉之后那次写入。
    ///
    /// 这些只有真实 Excel 验得出来：全 null 的快照、Shape 命名、区域相交
    /// 都是 COM 的取值语义，桩件复现不了。
    /// </summary>
    internal static class HonestUndoTests
    {
        internal static void Run(object excel, ToolExecutor executor, Action<string, bool, string> report)
        {
            TestChartReportsShapeName(excel, executor, report);
            TestChartUndoDeletesIt(excel, executor, report);
            TestChartCannotBeRedone(executor, report);
            TestMixedFormatRegistersNoUndo(excel, executor, report);
            TestPartiallyMixedFormatMarksIncomplete(excel, executor, report);
            TestClearKeepsContentUndoWhenFormatMixed(excel, executor, report);
            TestUndoDoesNotPaintMixedFillBlack(excel, executor, report);
            TestUndoDoesNotPaintDifferentColoursBlack(excel, executor, report);
            TestUndoRestoresGenuineBlackFill(excel, executor, report);
            TestUndoKeepsUniformNoFillEmpty(excel, executor, report);
            TestClearUndoKeepsNoFillEmpty(excel, executor, report);
            TestUndoKeepsFontFollowingTheme(excel, executor, report);
            TestFontNameAndUnderlineSurviveUndo(excel, executor, report);
            TestClearFormatsDisclosesBorderLoss(excel, executor, report);
            TestWithheldUndoAlwaysStatesReason(report);
            TestOverlappingUndoWarnsFirst(executor, report);
            TestNonOverlappingUndoNeedsNoConfirm(executor, report);
        }

        /// <summary>
        /// 建图必须回报 Excel 赋的 Shape 名——那正是撤销时 Shapes.Item 要用的键。
        /// 不回报的话撤销注定失败，而按钮照样会亮。
        /// </summary>
        private static void TestChartReportsShapeName(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            try
            {
                Seed(excel, "N1", 3);
                Seed(excel, "N2", 5);

                var result = executor.Execute(
                    "create_chart",
                    JObject.Parse(@"{""range"":""N1:N2"",""chart_type"":""column""}"),
                    "undo-chart-name");

                if (!result.Ok)
                {
                    report("建图回报 Shape 名称", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var data = JObject.FromObject(result.Data);
                var name = data.Value<string>("chart_name");
                report(
                    "建图回报 Shape 名称",
                    !string.IsNullOrWhiteSpace(name),
                    $"chart_name={name ?? "(空)"}");

                // 有名字就该给撤销入口；这条与下一项一起构成「按钮亮着就真能撤」。
                var record = executor.Undo.Find("undo-chart-name");
                report(
                    "有名字时登记了可撤销的记录",
                    record != null && record.CanUndo,
                    record == null ? "没有记录" : $"CanUndo={record.CanUndo}");
            }
            catch (Exception ex)
            {
                report("建图回报 Shape 名称", false, "抛出异常：" + ex.Message);
            }
        }

        /// <summary>撤销要真的把那张图删掉，而不是报「找不到图表「」」。</summary>
        private static void TestChartUndoDeletesIt(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            try
            {
                var before = ShapeCount(excel);
                var create = executor.Execute(
                    "create_chart",
                    JObject.Parse(@"{""range"":""N1:N2"",""chart_type"":""line""}"),
                    "undo-chart-delete");

                if (!create.Ok)
                {
                    report("撤销建图会删掉那张图", false, create.ErrorCode + " " + create.Error);
                    return;
                }

                var added = ShapeCount(excel);
                var outcome = executor.Undo.Undo("undo-chart-delete");
                var after = ShapeCount(excel);

                report(
                    "撤销建图会删掉那张图",
                    outcome.Ok && added == before + 1 && after == before,
                    $"{outcome.ErrorCode} {outcome.Message}；图形数 {before}→{added}→{after}");
            }
            catch (Exception ex)
            {
                report("撤销建图会删掉那张图", false, "抛出异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 图表删掉就重建不回来，所以撤销之后不得再显示「恢复」。
        /// 只修撤销不修恢复，等于把同一个谎言从一个按钮挪到另一个。
        /// </summary>
        private static void TestChartCannotBeRedone(
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            try
            {
                var record = executor.Undo.Find("undo-chart-delete");
                if (record == null)
                {
                    report("撤销建图后不提供恢复", false, "没有记录可查");
                    return;
                }

                report(
                    "撤销建图后不提供恢复",
                    record.Undone && !record.CanRedo,
                    $"Undone={record.Undone} CanRedo={record.CanRedo}");
            }
            catch (Exception ex)
            {
                report("撤销建图后不提供恢复", false, "抛出异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 外观属性逐项都不一致时，还原会把它们全部跳过——那条记录什么也还原不了。
        /// 因此不登记，而不是给一个点下去标成「已撤销」而格子毫无变化的按钮。
        /// </summary>
        private static void TestMixedFormatRegistersNoUndo(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-format-mixed";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                // 必须让全部九项都不一致，范围级读取才会每一项都返回 null。
                // 只改加粗和字号是不够的：斜体、换行、对齐仍然统一，
                // 那种快照还原得回一部分，属于下一条测试覆盖的「部分不一致」。
                MakeFullyMixed(sheet, "P1", "P2");

                // 判据必须在操作之前取。放在操作之后会被操作本身改掉——
                // format_range 把 bold 统一成 true，那一项就不再是混合值，
                // 于是「九项全不一致」永远差一项，而快照采集用的是操作前的状态。
                var probe = executor.IsFormattingMixed("P1:P2", null);
                report(
                    "操作前这片范围九项全不一致",
                    probe == true,
                    $"IsFormattingMixed={(probe.HasValue ? probe.Value.ToString() : "null")}");

                var result = executor.Execute(
                    "format_range",
                    JObject.Parse(@"{""range"":""P1:P2"",""bold"":true}"),
                    undoId);

                report(
                    "格式操作本身仍然成功",
                    result.Ok,
                    result.Ok ? string.Empty : result.ErrorCode + " " + result.Error);

                var record = executor.Undo.Find(undoId);
                report(
                    "格式逐项不一致时不登记撤销",
                    record == null,
                    record == null ? string.Empty : $"仍登记了记录 CanUndo={record.CanUndo}");
            }
            catch (Exception ex)
            {
                report("格式逐项不一致时不登记撤销", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 日常最常见的那一档：只有几项外观不一致（加粗与字号），其余统一。
        ///
        /// 这种快照还原得回一部分，所以按钮要给——撤回大半比一点撤不了有用——
        /// 但必须标明不完整，好让卡片说清哪些回不来。若按「全部九项都缺才算混合」
        /// 判断，这一档会被当成完整撤销，用户拿到的是一个自称能还原的按钮。
        /// </summary>
        private static void TestPartiallyMixedFormatMarksIncomplete(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-format-partial";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetBold(sheet, "S1", true);
                SetBold(sheet, "S2", false);
                SetFontSize(sheet, "S1", 15);
                SetFontSize(sheet, "S2", 10);

                var result = executor.Execute(
                    "format_range",
                    JObject.Parse(@"{""range"":""S1:S2"",""italic"":true}"),
                    undoId);

                if (!result.Ok)
                {
                    report("部分不一致时仍给撤销", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var record = executor.Undo.Find(undoId);
                report(
                    "部分不一致时仍给撤销",
                    record != null && record.CanUndo,
                    record == null ? "没有记录" : $"CanUndo={record.CanUndo}");

                report(
                    "部分不一致的记录标为不完整",
                    record?.Before != null && record.Before.FormatIncomplete,
                    record?.Before == null
                        ? "没有前快照"
                        : $"FormatIncomplete={record.Before.FormatIncomplete}");
            }
            catch (Exception ex)
            {
                report("部分不一致时仍给撤销", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 清除会丢值，值能逐格留底。所以格式不完整时仍要留下记录——
        /// 值找回来比格式要紧——但必须标出格式还原不了，好让卡片如实说。
        /// </summary>
        private static void TestClearKeepsContentUndoWhenFormatMixed(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-clear-mixed";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetCellValue(sheet, "Q1", "要清掉的甲");
                SetCellValue(sheet, "Q2", "要清掉的乙");

                // 这里只让部分属性不一致（加粗与字号），其余保持统一——
                // 这正是日常最常见的形态，也是「还原得回一部分」那一档。
                SetBold(sheet, "Q1", true);
                SetBold(sheet, "Q2", false);
                SetFontSize(sheet, "Q1", 16);
                SetFontSize(sheet, "Q2", 8);

                var result = executor.Execute(
                    "clear_range",
                    JObject.Parse(@"{""range"":""Q1:Q2"",""scope"":""all""}"),
                    undoId);

                if (!result.Ok)
                {
                    report("清除在格式混合时仍可撤内容", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var record = executor.Undo.Find(undoId);
                report(
                    "清除在格式混合时仍登记记录",
                    record != null && record.CanUndo,
                    record == null ? "没有记录" : $"CanUndo={record.CanUndo}");

                report(
                    "记录标出格式无法完整还原",
                    record?.Before != null && record.Before.FormatIncomplete,
                    record?.Before == null
                        ? "没有前快照"
                        : $"FormatIncomplete={record.Before.FormatIncomplete}");

                // 值必须真的找回来——这才是留下这条记录的理由。
                if (record != null && record.CanUndo)
                {
                    var outcome = executor.Undo.Undo(undoId);
                    var q1 = Convert.ToString(ReadCell(sheet, "Q1"));
                    report(
                        "撤销把清掉的值找了回来",
                        outcome.Ok && q1 == "要清掉的甲",
                        $"{outcome.ErrorCode} {outcome.Message}；Q1={q1}");
                }
            }
            catch (Exception ex)
            {
                report("清除在格式混合时仍可撤内容", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 撤销不许把混合填充刷成黑底。
        ///
        /// 一片有的格子有填充、有的没有（带高亮行的表格，最常见的形态之一），
        /// 宿主对范围级 Interior.Color 返回的是 0 而不是 DBNull——那是它表示
        /// 「这一项不统一」的另一种说法。0 不是 null，跳过缺失值的守卫拦不住它，
        /// 于是还原会把 Color 真的写成 0，整片变黑。
        ///
        /// 这比「撤销没生效」严重：它让撤销成为一次新的破坏，而用户点它
        /// 正是为了回到原样。Pattern 与 Color 是一对，Pattern 读不出统一值时
        /// Color 也不可信。
        /// </summary>
        private static void TestUndoDoesNotPaintMixedFillBlack(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-mixed-fill";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                // 一格无填充、一格黄色：范围级 Pattern 不统一，Color 读成 0。
                SetNoFill(sheet, "W1");
                SetFill(sheet, "W2", 0x00FFFF);

                var before1 = FillColor(sheet, "W1");
                var before2 = FillColor(sheet, "W2");

                var result = executor.Execute(
                    "format_range",
                    JObject.Parse(@"{""range"":""W1:W2"",""bold"":true}"),
                    undoId);

                if (!result.Ok)
                {
                    report("撤销不把混合填充刷成黑底", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var record = executor.Undo.Find(undoId);
                if (record == null || !record.CanUndo)
                {
                    // 没给撤销入口也是一种诚实的处置，此时无从刷黑。
                    report(
                        "撤销不把混合填充刷成黑底",
                        true,
                        "未登记撤销记录，不存在刷黑的机会");
                    return;
                }

                executor.Undo.Undo(undoId);

                var after1 = FillColor(sheet, "W1");
                var after2 = FillColor(sheet, "W2");

                // 黑色是 0。撤销后任何一格变成黑底，就是把 0 当成了真实颜色写回去。
                report(
                    "撤销不把混合填充刷成黑底",
                    after1 != 0 && after2 != 0,
                    $"W1 {before1}→{after1}，W2 {before2}→{after2}（0 表示黑底）");

                // 更强的一条：无填充的那格应当仍然没有填充。
                report(
                    "原本无填充的那格撤销后仍无填充",
                    FillPattern(sheet, "W1") == -4142,
                    $"W1 Pattern={FillPattern(sheet, "W1")}（-4142 为无填充）");
            }
            catch (Exception ex)
            {
                report("撤销不把混合填充刷成黑底", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 两格都有填充、只是颜色不同时，撤销也不许刷成黑底。
        ///
        /// 这一条比上一条更难拦：实测宿主此时给出 Pattern=1（统一的实心）、Color=0，
        /// 而「整片真的是黑色」的读数**逐字相同**。所以既不能只看 Color 是不是缺失
        /// （0 不是缺失值），也不能只看 Pattern（它是统一的，守卫会放行）。
        /// 判据只能落在 ColorIndex 上——颜色不统一时它是 DBNull，真黑时是 1。
        /// </summary>
        private static void TestUndoDoesNotPaintDifferentColoursBlack(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-two-colours";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                // 都实心，颜色不同。Pattern 统一，Color 骗人。
                SetFill(sheet, "X1", 0x00FFFF);
                SetFill(sheet, "X2", 0xFFFF00);

                var result = executor.Execute(
                    "format_range",
                    JObject.Parse(@"{""range"":""X1:X2"",""bold"":true}"),
                    undoId);

                if (!result.Ok)
                {
                    report("颜色不同时撤销不刷黑", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var record = executor.Undo.Find(undoId);
                if (record == null || !record.CanUndo)
                {
                    report("颜色不同时撤销不刷黑", true, "未登记撤销记录，不存在刷黑的机会");
                    return;
                }

                executor.Undo.Undo(undoId);

                var after1 = FillColor(sheet, "X1");
                var after2 = FillColor(sheet, "X2");

                report(
                    "颜色不同时撤销不刷黑",
                    after1 != 0 && after2 != 0,
                    $"X1={after1}，X2={after2}（0 表示黑底）");

                // 更强的一条：两格应各自保留原色，而不是被抹成同一个颜色。
                report(
                    "两格各自保留原色",
                    Math.Abs(after1 - 0x00FFFF) < 1 && Math.Abs(after2 - 0xFFFF00) < 1,
                    $"X1={after1}（期望 {0x00FFFF}），X2={after2}（期望 {0xFFFF00}）");
            }
            catch (Exception ex)
            {
                report("颜色不同时撤销不刷黑", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 真的黑色填充必须还原回黑色。
        ///
        /// 上一条的反向守卫：为了不刷黑而一律跳过 Color，会让用户真正设过的黑色
        /// 填充撤销不回来——那是修过头。整片真黑时 ColorIndex 是 1 而不是 DBNull，
        /// 判据必须放它过去。
        /// </summary>
        private static void TestUndoRestoresGenuineBlackFill(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-real-black";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                // 整片真黑，然后让操作把它改成别的颜色，撤销应回到黑色。
                SetFill(sheet, "Y1", 0);
                SetFill(sheet, "Y2", 0);

                var result = executor.Execute(
                    "format_range",
                    JObject.Parse(@"{""range"":""Y1:Y2"",""fill_color"":""#FF0000""}"),
                    undoId);

                if (!result.Ok)
                {
                    report("真黑色填充能撤销回黑色", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var changed = FillColor(sheet, "Y1");
                var record = executor.Undo.Find(undoId);
                if (record == null || !record.CanUndo)
                {
                    report("真黑色填充能撤销回黑色", false, "没有登记撤销记录，真实设过的黑色撤不回来");
                    return;
                }

                var outcome = executor.Undo.Undo(undoId);
                var after1 = FillColor(sheet, "Y1");
                var after2 = FillColor(sheet, "Y2");

                report(
                    "真黑色填充能撤销回黑色",
                    outcome.Ok && after1 == 0 && after2 == 0,
                    $"{outcome.ErrorCode} {outcome.Message}；改成 {changed} 后撤销得到 Y1={after1}、Y2={after2}");
            }
            catch (Exception ex)
            {
                report("真黑色填充能撤销回黑色", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 均匀无填充的范围，撤销后必须仍然无填充。
        ///
        /// 这是最常见的范围形态（任何一张没上底色的表），也是三条颜色测试都漏掉的：
        /// 混合、双色、真黑测的都是「有填充」的情形。实测均匀无填充时
        /// Pattern=-4142、ColorIndex=-4142、Color=16777215——**三项全是真实值**，
        /// 任何「缺失才跳过」的判据都会放行，而写回 Color 会把 Pattern 顶成实心。
        /// 后果是撤销把一张普通表刷成实心白底、网格线消失。
        /// </summary>
        private static void TestUndoKeepsUniformNoFillEmpty(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-uniform-nofill";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                // 整片显式设成无填充，模拟一张没上过底色的表。
                SetNoFill(sheet, "AA1");
                SetNoFill(sheet, "AA2");

                var before = FillPattern(sheet, "AA1");
                if (before != -4142)
                {
                    report("均匀无填充撤销后仍无填充", false, $"造范围失败，Pattern={before}");
                    return;
                }

                var result = executor.Execute(
                    "format_range",
                    JObject.Parse(@"{""range"":""AA1:AA2"",""bold"":true}"),
                    undoId);

                if (!result.Ok)
                {
                    report("均匀无填充撤销后仍无填充", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var record = executor.Undo.Find(undoId);
                if (record == null || !record.CanUndo)
                {
                    report("均匀无填充撤销后仍无填充", false, "没有登记撤销记录，加粗撤不回来");
                    return;
                }

                var outcome = executor.Undo.Undo(undoId);
                var after1 = FillPattern(sheet, "AA1");
                var after2 = FillPattern(sheet, "AA2");

                report(
                    "均匀无填充撤销后仍无填充",
                    outcome.Ok && after1 == -4142 && after2 == -4142,
                    $"{outcome.ErrorCode} {outcome.Message}；Pattern {before}→ AA1={after1}、AA2={after2}"
                        + "（-4142=无填充，1=实心）");
            }
            catch (Exception ex)
            {
                report("均匀无填充撤销后仍无填充", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 清除之后撤销，同样不许把无填充变成实心。
        ///
        /// 比上一条更要紧：清除会先把填充抹掉，所以还原是唯一往回放填充的一步。
        /// 这一步造出一层原本不存在的实心底，用户没有第二次机会发现。
        /// </summary>
        private static void TestClearUndoKeepsNoFillEmpty(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-clear-nofill";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetNoFill(sheet, "AB1");
                SetNoFill(sheet, "AB2");
                SetCellValue(sheet, "AB1", "要清的");
                SetCellValue(sheet, "AB2", "也要清");

                var result = executor.Execute(
                    "clear_range",
                    JObject.Parse(@"{""range"":""AB1:AB2"",""scope"":""all""}"),
                    undoId);

                if (!result.Ok)
                {
                    report("清除撤销后不凭空造出填充", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var record = executor.Undo.Find(undoId);
                if (record == null || !record.CanUndo)
                {
                    report("清除撤销后不凭空造出填充", false, "没有登记撤销记录，清掉的值找不回来");
                    return;
                }

                var outcome = executor.Undo.Undo(undoId);
                var pattern1 = FillPattern(sheet, "AB1");
                var value1 = Convert.ToString(ReadCell(sheet, "AB1"));

                report(
                    "清除撤销后不凭空造出填充",
                    outcome.Ok && pattern1 == -4142,
                    $"{outcome.ErrorCode} {outcome.Message}；AB1 Pattern={pattern1}（-4142=无填充）");

                // 值仍要找回来——不能为了不造填充而把内容还原也跳过。
                report(
                    "清除撤销仍把值找回来",
                    value1 == "要清的",
                    $"AB1={value1}");
            }
            catch (Exception ex)
            {
                report("清除撤销后不凭空造出填充", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        private static double FillColor(object sheet, string address)
        {
            object cell = null;
            object interior = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                interior = Com.Get(cell, "Interior");
                return Convert.ToDouble(Com.Get(interior, "Color"));
            }
            finally
            {
                Com.Release(interior);
                Com.Release(cell);
            }
        }

        private static int FillPattern(object sheet, string address)
        {
            object cell = null;
            object interior = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                interior = Com.Get(cell, "Interior");
                return Convert.ToInt32(Com.Get(interior, "Pattern"));
            }
            finally
            {
                Com.Release(interior);
                Com.Release(cell);
            }
        }

        /// <summary>
        /// 跟随主题的字色，撤销后必须还跟随主题。
        ///
        /// 「跟随主题」与「显式黑色」的 Color 和 ColorIndex 逐字相同（都是 0 和 1），
        /// 只有 ThemeColor 分得开（2 对空）。把采到的 Color=0 原样写回会把
        /// ThemeColor 打成 null——颜色看着没变，联动没了。换主题或在深色模式下
        /// 才看得出来，那时已经无从追溯是哪一次撤销做的。
        /// 与「无填充→实心」同一类：读数是个具体值，却不代表真实状态。
        /// </summary>
        private static void TestUndoKeepsFontFollowingTheme(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-font-theme";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetCellValue(sheet, "AC1", "跟随主题的文字");
                SetCellValue(sheet, "AC2", "也跟随主题");

                var themeBefore = FontThemeColor(sheet, "AC1");
                if (themeBefore == null)
                {
                    report("跟随主题的字色撤销后仍跟随主题", false, "造范围失败：初始就没有主题联动");
                    return;
                }

                // 让操作把字色改成显式红色。
                var result = executor.Execute(
                    "format_range",
                    JObject.Parse(@"{""range"":""AC1:AC2"",""font_color"":""#FF0000""}"),
                    undoId);

                if (!result.Ok)
                {
                    report("跟随主题的字色撤销后仍跟随主题", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var record = executor.Undo.Find(undoId);
                if (record == null || !record.CanUndo)
                {
                    report("跟随主题的字色撤销后仍跟随主题", false, "没有登记撤销记录");
                    return;
                }

                var outcome = executor.Undo.Undo(undoId);
                var themeAfter = FontThemeColor(sheet, "AC1");
                var colorAfter = FontColorOf(sheet, "AC1");

                report(
                    "跟随主题的字色撤销后仍跟随主题",
                    outcome.Ok && themeAfter != null,
                    $"{outcome.ErrorCode} {outcome.Message}；ThemeColor {themeBefore}→{themeAfter ?? "(空)"}");

                // 颜色本身也要撤回来——不能为了保联动而让红色留在那儿。
                report(
                    "字色也确实撤了回来",
                    Math.Abs(colorAfter) < 1,
                    $"Color={colorAfter}（期望 0）");
            }
            catch (Exception ex)
            {
                report("跟随主题的字色撤销后仍跟随主题", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 字体名与下划线要撤得回来。
        ///
        /// 这三项（字体名、下划线、删除线）此前完全不在快照里：清除格式把它们
        /// 一并重置，而撤销只还原九项，于是宋体变回等线、下划线消失，
        /// 卡片却说操作已撤销。三项都是单值读取，各一次 COM，没有不采的理由。
        /// </summary>
        private static void TestFontNameAndUnderlineSurviveUndo(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-font-name";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetCellValue(sheet, "AD1", "带下划线的宋体");
                SetFontName(sheet, "AD1", "宋体");
                SetUnderline(sheet, "AD1", 2);   // xlUnderlineStyleSingle

                var nameBefore = FontNameOf(sheet, "AD1");
                var underlineBefore = UnderlineOf(sheet, "AD1");

                var result = executor.Execute(
                    "clear_range",
                    JObject.Parse(@"{""range"":""AD1"",""scope"":""formats""}"),
                    undoId);

                if (!result.Ok)
                {
                    report("字体名与下划线撤得回来", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var record = executor.Undo.Find(undoId);
                if (record == null || !record.CanUndo)
                {
                    report("字体名与下划线撤得回来", false, "没有登记撤销记录");
                    return;
                }

                executor.Undo.Undo(undoId);

                var nameAfter = FontNameOf(sheet, "AD1");
                var underlineAfter = UnderlineOf(sheet, "AD1");

                report(
                    "字体名撤得回来",
                    nameAfter == nameBefore,
                    $"{nameBefore} → {nameAfter}");
                report(
                    "下划线撤得回来",
                    underlineAfter == underlineBefore,
                    $"{underlineBefore} → {underlineAfter}");
            }
            catch (Exception ex)
            {
                report("字体名与下划线撤得回来", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 清除格式会抹掉边框，而边框不在快照里——卡片必须说出来。
        ///
        /// 边框逐边读的 COM 成本远高于其余九项之和，所以不采。但撤销之后
        /// 边框永久消失，如果卡片什么都不说，用户会以为已经完全回退。
        /// 做不到的部分要写在卡上，这是本项目一贯的取舍。
        /// </summary>
        private static void TestClearFormatsDisclosesBorderLoss(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-clear-borders";
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetCellValue(sheet, "AE1", "有边框");
                AddBorder(sheet, "AE1");

                var borderBefore = TopBorderStyle(sheet, "AE1");
                if (borderBefore == -4142)
                {
                    report("清除格式说明边框不会回来", false, "造范围失败：边框没加上");
                    return;
                }

                var result = executor.Execute(
                    "clear_range",
                    JObject.Parse(@"{""range"":""AE1"",""scope"":""formats""}"),
                    undoId);

                if (!result.Ok)
                {
                    report("清除格式说明边框不会回来", false, result.ErrorCode + " " + result.Error);
                    return;
                }

                var record = executor.Undo.Find(undoId);
                report(
                    "清除格式的快照标出有采不到的维度",
                    record?.Before != null && record.Before.ClearsUncapturedFormats,
                    record?.Before == null
                        ? "没有前快照"
                        : $"ClearsUncapturedFormats={record.Before.ClearsUncapturedFormats}");

                var note = AgentRunner.UndoNoteForTest("clear_range", result, record) ?? string.Empty;
                report(
                    "卡片说明边框不会回来",
                    note.Contains("边框"),
                    note.Length == 0 ? "(空)" : note);

                // 只清内容时不该报边框——那次操作压根没碰边框。
                var contentsOnly = executor.Execute(
                    "clear_range",
                    JObject.Parse(@"{""range"":""AE2"",""scope"":""contents""}"),
                    "undo-clear-contents-only");
                var contentsRecord = executor.Undo.Find("undo-clear-contents-only");
                var contentsNote = AgentRunner.UndoNoteForTest("clear_range", contentsOnly, contentsRecord);
                report(
                    "只清内容时不提边框",
                    contentsNote == null || !contentsNote.Contains("边框"),
                    contentsNote ?? "(空)");
            }
            catch (Exception ex)
            {
                report("清除格式说明边框不会回来", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        private static void SetFontName(object sheet, string address, string name)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                Com.Set(font, "Name", name);
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        private static string FontNameOf(object sheet, string address)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                return Com.GetString(font, "Name");
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        private static void SetUnderline(object sheet, string address, int style)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                Com.Set(font, "Underline", style);
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        private static int UnderlineOf(object sheet, string address)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                return Convert.ToInt32(Com.Get(font, "Underline"));
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        private static void AddBorder(object sheet, string address)
        {
            object cell = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                Com.Call(cell, "BorderAround", 1, 2);   // xlContinuous, xlMedium
            }
            finally
            {
                Com.Release(cell);
            }
        }

        private static int TopBorderStyle(object sheet, string address)
        {
            object cell = null;
            object borders = null;
            object edge = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                borders = Com.Get(cell, "Borders");
                edge = Com.Get(borders, "Item", 8);   // xlEdgeTop
                return Convert.ToInt32(Com.Get(edge, "LineStyle"));
            }
            catch
            {
                return -4142;
            }
            finally
            {
                Com.Release(edge);
                Com.Release(borders);
                Com.Release(cell);
            }
        }

        /// <summary>读 ThemeColor。没有主题联动时宿主给空，此处统一成 null。</summary>
        private static string FontThemeColor(object sheet, string address)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                if (!Com.TryGet(font, "ThemeColor", out var value) || value == null || value is DBNull)
                {
                    return null;
                }

                return Convert.ToString(value);
            }
            catch
            {
                return null;
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        private static double FontColorOf(object sheet, string address)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                return Convert.ToDouble(Com.Get(font, "Color"));
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        /// <summary>
        /// 凡是不给撤销按钮，宿主都要给出原因。
        ///
        /// 缺按钮本身是可见的，缺原因会被当成故障——而它其实是「保不住足以完整
        /// 还原的依据就不承诺撤销」这一有意为之的取舍。面板「适配」早就这样做，
        /// 模型发起的这条路上曾经两种情形（无名图表、格式全项不一致）都是静默的。
        ///
        /// 这里验的是**宿主产出**，而不是面板会不会渲染：面板单测喂进去的
        /// undoNote 是自己造的，宿主不产出的话那条测试照样全绿。
        /// </summary>
        private static void TestWithheldUndoAlwaysStatesReason(Action<string, bool, string> report)
        {
            var ok = ToolResult.Success(new System.Collections.Generic.Dictionary<string, object>
            {
                ["sheet"] = "Sheet1",
                ["address"] = "$A$1",
            });

            // 一、成功但根本没有登记记录（快照采不到）。
            foreach (var tool in new[] { "create_chart", "format_range", "write_values", "clear_range" })
            {
                var note = AgentRunner.UndoNoteForTest(tool, ok, null);
                report(
                    $"{tool} 没有记录时说明原因",
                    !string.IsNullOrWhiteSpace(note),
                    note ?? "(空)");
            }

            // 二、有记录但撤不了（无名图表就是这种：Structure 与 Before 都是空）。
            // Structure 与 Before 都空的记录，正是无名图表留下的那种。
            var dead = new UndoRecord { Id = "x", ToolName = "create_chart" };
            var deadNote = AgentRunner.UndoNoteForTest("create_chart", ok, dead);
            report(
                "记录存在但撤不了时也说明原因",
                !dead.CanUndo && !string.IsNullOrWhiteSpace(deadNote),
                $"CanUndo={dead.CanUndo}，说明={deadNote ?? "(空)"}");

            // 三、建图的原因要点出「名称」，否则用户不知道该怎么办。
            var chartNote = AgentRunner.UndoNoteForTest("create_chart", ok, null) ?? string.Empty;
            report(
                "建图的原因点出名称问题",
                chartNote.Contains("名称"),
                chartNote);

            // 四、真能撤销时不要多嘴。撤销入口就在那儿，再解释一句是噪声。
            var live = new UndoRecord
            {
                Id = "y",
                ToolName = "write_values",
                Before = new RangeSnapshot { SheetName = "Sheet1", Address = "$A$1", Rows = 1, Columns = 1 },
            };
            report(
                "能撤销时不追加说明",
                AgentRunner.UndoNoteForTest("write_values", ok, live) == null,
                AgentRunner.UndoNoteForTest("write_values", ok, live) ?? "(空)");

            // 五、只读工具缺撤销入口是本来如此，不该解释。
            report(
                "只读工具不解释缺撤销",
                AgentRunner.UndoNoteForTest("read_range", ok, null) == null,
                AgentRunner.UndoNoteForTest("read_range", ok, null) ?? "(空)");

            // 六、操作本身失败时不说撤销的事——失败原因已经在卡上了。
            var failed = ToolResult.Failure("BOOM", "宿主拒绝");
            report(
                "操作失败时不谈撤销",
                AgentRunner.UndoNoteForTest("format_range", failed, null) == null,
                AgentRunner.UndoNoteForTest("format_range", failed, null) ?? "(空)");

        }

        /// <summary>
        /// 两次写入范围相交时，撤较早那次会盖掉较晚那次。
        /// 第一次点击必须拒绝并说清，再点一次才执行——乱序撤销本身是允许的，
        /// 要拦的只是「静默覆盖」。
        /// </summary>
        private static void TestOverlappingUndoWarnsFirst(
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            try
            {
                var first = executor.Execute(
                    "write_values",
                    JObject.Parse(@"{""range"":""R1:R4"",""values"":[[1],[2],[3],[4]]}"),
                    "undo-overlap-early");

                var second = executor.Execute(
                    "write_values",
                    JObject.Parse(@"{""range"":""R2:R3"",""values"":[[99],[98]]}"),
                    "undo-overlap-late");

                if (!first.Ok || !second.Ok)
                {
                    report("相交的撤销先给警告", false, "准备写入失败");
                    return;
                }

                var warned = executor.Undo.Undo("undo-overlap-early");
                report(
                    "相交的撤销先给警告",
                    !warned.Ok && warned.ErrorCode == "OVERLAP_WARNING",
                    $"{warned.ErrorCode} {warned.Message}");

                report(
                    "警告里点出被覆盖的是哪一次",
                    (warned.Message ?? string.Empty).Contains("写入值"),
                    warned.Message);

                // 警告不得顺手改状态：还没撤，记录必须仍可撤销。
                var record = executor.Undo.Find("undo-overlap-early");
                report(
                    "给过警告后记录仍未被标成已撤销",
                    record != null && !record.Undone && record.CanUndo,
                    record == null ? "没有记录" : $"Undone={record.Undone}");

                // 明确再点一次才执行。
                var forced = executor.Undo.Undo("undo-overlap-early", force: true);
                report(
                    "确认之后才真的撤销",
                    forced.Ok && forced.Undone,
                    $"{forced.ErrorCode} {forced.Message}");
            }
            catch (Exception ex)
            {
                report("相交的撤销先给警告", false, "抛出异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 不相交的两次写入互不相干，第一次点击就该撤销成功。
        /// 这条是上一条的对照：警告不能变成「凡是有后续写入就拦」。
        /// </summary>
        private static void TestNonOverlappingUndoNeedsNoConfirm(
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            try
            {
                var first = executor.Execute(
                    "write_values",
                    JObject.Parse(@"{""range"":""T1:T2"",""values"":[[""甲""],[""乙""]]}"),
                    "undo-disjoint-early");

                var second = executor.Execute(
                    "write_values",
                    JObject.Parse(@"{""range"":""V1:V2"",""values"":[[""丙""],[""丁""]]}"),
                    "undo-disjoint-late");

                if (!first.Ok || !second.Ok)
                {
                    report("不相交的撤销无需确认", false, "准备写入失败");
                    return;
                }

                var outcome = executor.Undo.Undo("undo-disjoint-early");
                report(
                    "不相交的撤销无需确认",
                    outcome.Ok && outcome.Undone,
                    $"{outcome.ErrorCode} {outcome.Message}");
            }
            catch (Exception ex)
            {
                report("不相交的撤销无需确认", false, "抛出异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 让两格的全部九项外观都不同，逼出「范围级每一项都返回 null」的快照。
        ///
        /// 九项缺一不可：只要有一项统一，那一项就还原得回去，
        /// 快照便不属于「什么也还原不了」这一档。
        /// </summary>
        private static void MakeFullyMixed(object sheet, string first, string second)
        {
            SetBold(sheet, first, true);
            SetBold(sheet, second, false);
            SetItalic(sheet, first, true);
            SetItalic(sheet, second, false);
            SetFontSize(sheet, first, 14);
            SetFontSize(sheet, second, 9);
            SetFontColor(sheet, first, 0xFF0000);
            SetFontColor(sheet, second, 0x0000FF);
            // 填充要连 Pattern 一起错开：给两格都设颜色的话 Pattern 都成了实心，
            // 那一项就是统一值，「九项全缺」永远凑不齐。一格无填充、一格实心，
            // Pattern 与 Color 才同时不一致。
            SetNoFill(sheet, first);
            SetFill(sheet, second, 0x00FF00);
            SetWrap(sheet, first, true);
            SetWrap(sheet, second, false);

            // 对齐用具体值，不能留「常规」：两格都是常规就成了统一值。
            SetAlignment(sheet, first, -4131, -4160);   // 左、顶
            SetAlignment(sheet, second, -4152, -4107);  // 右、底
        }

        private static int ShapeCount(object excel)
        {
            object sheet = null;
            object shapes = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");
                shapes = Com.Get(sheet, "Shapes");
                return Convert.ToInt32(Com.Get(shapes, "Count"));
            }
            finally
            {
                Com.Release(shapes);
                Com.Release(sheet);
            }
        }

        private static void Seed(object excel, string address, object value)
        {
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");
                SetCellValue(sheet, address, value);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        private static void SetCellValue(object sheet, string address, object value)
        {
            object cell = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                Com.Set(cell, "Value2", value);
            }
            finally
            {
                Com.Release(cell);
            }
        }

        private static object ReadCell(object sheet, string address)
        {
            object cell = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                return Com.Get(cell, "Value2");
            }
            finally
            {
                Com.Release(cell);
            }
        }

        private static void SetBold(object sheet, string address, bool bold)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                Com.Set(font, "Bold", bold);
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        private static void SetFontSize(object sheet, string address, double size)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                Com.Set(font, "Size", size);
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        private static void SetFill(object sheet, string address, int color)
        {
            object cell = null;
            object interior = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                interior = Com.Get(cell, "Interior");
                Com.Set(interior, "Color", color);
            }
            finally
            {
                Com.Release(interior);
                Com.Release(cell);
            }
        }

        /// <summary>清掉填充，让 Interior.Pattern 变成 xlNone 而不是实心。</summary>
        private static void SetNoFill(object sheet, string address)
        {
            object cell = null;
            object interior = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                interior = Com.Get(cell, "Interior");
                Com.Set(interior, "Pattern", -4142);  // xlNone
            }
            finally
            {
                Com.Release(interior);
                Com.Release(cell);
            }
        }

        private static void SetItalic(object sheet, string address, bool italic)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                Com.Set(font, "Italic", italic);
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        private static void SetFontColor(object sheet, string address, int color)
        {
            object cell = null;
            object font = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                font = Com.Get(cell, "Font");
                Com.Set(font, "Color", color);
            }
            finally
            {
                Com.Release(font);
                Com.Release(cell);
            }
        }

        private static void SetWrap(object sheet, string address, bool wrap)
        {
            object cell = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                Com.Set(cell, "WrapText", wrap);
            }
            finally
            {
                Com.Release(cell);
            }
        }

        private static void SetAlignment(object sheet, string address, int horizontal, int vertical)
        {
            object cell = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                Com.Set(cell, "HorizontalAlignment", horizontal);
                Com.Set(cell, "VerticalAlignment", vertical);
            }
            finally
            {
                Com.Release(cell);
            }
        }
    }
}
