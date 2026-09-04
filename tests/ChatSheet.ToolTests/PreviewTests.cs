using System;
using ChatSheet.AddIn.Agent;
using ChatSheet.AddIn.Hosts;
using ChatSheet.AddIn.Tools;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 审批卡对照的截断与空值语义。
    ///
    /// 这层要守的是三件容易在实现里被合并掉的事：
    ///   一、空单元格与「读不到当前值」必须分得开——前者是一次正常写入，
    ///       后者是探测失败，合成一个样子会让用户把后者看成前者；
    ///   二、截断要报剩下的**格数**，不是行数：截断同时发生在行与列两个方向；
    ///   三、上限取在面板扫得完，而不是工具层的 5000 格。
    /// </summary>
    internal static class PreviewTests
    {
        internal static void Run(Action<string, bool, string> report)
        {
            TestPairsCurrentAgainstIncoming(report);
            TestEmptyIsNotUnreadable(report);
            TestTruncation(report);
            TestMissingCurrentMarksUnreadable(report);
            TestFormulaGetsMoreRoom(report);
            TestMergeListsDiscardedValues(report);
            TestClearListsRemovedValues(report);
        }

        /// <summary>
        /// 对照表必须显示格子里的那一串，而不是底层值。
        ///
        /// Value2 把日期变成序列号（45900）、时间变成小数（0.75）、
        /// 百分比变成 0.1234。摆在「将改为 2025-08-31」旁边看起来像换了一种东西。
        /// Range.Text 给的就是屏幕上那一串，四种格式一次全对。
        /// 这条必须走真实 Excel：序列号与显示文本的对应关系是宿主语义，桩件复现不了。
        /// </summary>
        internal static void RunDisplayReads(object excel, ToolExecutor executor, Action<string, bool, string> report)
        {
            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetFormatted(sheet, "AF1", 45900, "yyyy-mm-dd");
                SetFormatted(sheet, "AF2", 0.75, "hh:mm");
                SetFormatted(sheet, "AF3", 0.1234, "0.0%");
                SetFormatted(sheet, "AF4", 1234.5, "#,##0.00");
                SetFormatted(sheet, "AF5", "纯文本", "@");

                var shown = executor.ReadDisplayMatrix("AF1:AF5", null, ChangePreviewBuilder.MaxRows, ChangePreviewBuilder.MaxColumns);
                if (shown == null || shown.Count < 5)
                {
                    report("对照按显示文本读日期", false, shown == null ? "返回空" : $"只读到 {shown.Count} 行");
                    return;
                }

                string Shown(int r)
                {
                    return Convert.ToString(shown[r][0] ?? string.Empty);
                }

                report(
                    "日期显示成日期而不是序列号",
                    Shown(0).IndexOf("2025", StringComparison.Ordinal) >= 0 && Shown(0).IndexOf("45900", StringComparison.Ordinal) < 0,
                    $"AF1={Shown(0)}");
                report(
                    "时间显示成时间而不是小数",
                    Shown(1).IndexOf("18", StringComparison.Ordinal) >= 0 && Shown(1) != "0.75",
                    $"AF2={Shown(1)}");
                report(
                    "百分比显示成百分比而不是小数",
                    Shown(2).IndexOf("%", StringComparison.Ordinal) >= 0,
                    $"AF3={Shown(2)}");
                report(
                    "千分位显示成千分位",
                    Shown(3).IndexOf(",", StringComparison.Ordinal) >= 0 || Shown(3).Contains("1234.50") || Shown(3).Contains("1,234.50"),
                    $"AF4={Shown(3)}");
                report(
                    "普通文本原样保留",
                    Shown(4) == "纯文本",
                    $"AF5={Shown(4)}");
            }
            catch (Exception ex)
            {
                report("对照按显示文本读日期", false, "抛出异常：" + ex.Message);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        private static void SetFormatted(object sheet, string address, object value, string format)
        {
            object cell = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                Com.Set(cell, "Value2", value);
                Com.Set(cell, "NumberFormat", format);
            }
            finally
            {
                Com.Release(cell);
            }
        }

        /// <summary>
        /// 对照逐格配对当前值与新值。

        /// <summary>
        /// 公式比普通值给更多字。
        ///
        /// `=IFERROR(VLOOKUP($A2,Sheet2!$A:$D,4,FALSE),"未找到")` 是 51 个字符，
        /// 截到 40 恰好切掉关键部分。而「批准前看得见要改什么」对公式反而最要紧：
        /// 值错了看得出来，公式错了要跑一遍才知道。
        /// </summary>
        private static void TestFormulaGetsMoreRoom(Action<string, bool, string> report)
        {
            const string formula = "=IFERROR(VLOOKUP($A2,Sheet2!$A:$D,4,FALSE),\"未找到\")";

            var asFormula = ChangePreviewBuilder.Build(
                null, JArray.Parse($@"[[""{formula.Replace("\"", "\\\"")}""]]"), 1, formulas: true);
            var asValue = ChangePreviewBuilder.Build(
                null, JArray.Parse($@"[[""{formula.Replace("\"", "\\\"")}""]]"), 1, formulas: false);

            if (asFormula == null || asValue == null || asFormula.Cells.Count == 0)
            {
                report("公式比普通值给更多字", false, "未生成对照");
                return;
            }

            report(
                "五十来字符的公式不被截断",
                !asFormula.Cells[0].After.EndsWith("…", StringComparison.Ordinal),
                $"长度 {asFormula.Cells[0].After.Length}，原文 {formula.Length}");

            report(
                "同一段文本按普通值处理时仍会截断",
                asValue.Cells[0].After.EndsWith("…", StringComparison.Ordinal),
                $"长度 {asValue.Cells[0].After.Length}");

            report(
                "公式上限确实比普通值宽",
                ChangePreviewBuilder.MaxFormulaText > ChangePreviewBuilder.MaxCellText,
                $"公式 {ChangePreviewBuilder.MaxFormulaText} vs 普通 {ChangePreviewBuilder.MaxCellText}");

            // 再长的公式仍要截，且截断可见。
            var huge = "=" + new string('A', 300);
            var capped = ChangePreviewBuilder.Build(null, JArray.Parse($@"[[""{huge}""]]"), 1, formulas: true);
            report(
                "超长公式仍按公式上限截断",
                capped != null
                    && capped.Cells[0].After.Length == ChangePreviewBuilder.MaxFormulaText + 1
                    && capped.Cells[0].After.EndsWith("…", StringComparison.Ordinal),
                capped == null ? "未生成" : $"长度 {capped.Cells[0].After.Length}");
        }

        /// <summary>
        /// 合并要列出会丢哪些值，并且不把左上角算进去。
        ///
        /// 合并是唯一静默丢值的写操作，事后没有痕迹可查。工具会把 discarded_values
        /// 回给模型，但那发生在用户点「允许」之后——所以这个数字必须先出现在卡上。
        /// </summary>
        private static void TestMergeListsDiscardedValues(Action<string, bool, string> report)
        {
            // 左上角「标题」保得住；其余三个有值的会丢；空格不算。
            var current = JArray.Parse(@"[[""标题"",""甲"",""""],[""乙"",null,""丙""]]");

            var preview = ChangePreviewBuilder.BuildDiscard(current, totalCells: 6, keepAnchor: true, kind: "merge");
            if (preview == null)
            {
                report("合并列出会丢的值", false, "未生成对照");
                return;
            }

            report(
                "合并只数会丢的那些有值格",
                preview.DiscardedValues == 3,
                $"discardedValues={preview.DiscardedValues}，期望 3（甲、乙、丙）");

            report(
                "左上角不算作会丢",
                !preview.Cells.Exists(c => c.Row == 1 && c.Column == 1),
                "左上角出现在会丢的清单里");

            report(
                "空单元格不算作会丢",
                preview.Cells.TrueForAll(c => !c.BeforeEmpty),
                "有空格被算成会丢");

            report("对照标明是合并", preview.Kind == "merge", preview.Kind);

            // 丢的那一侧写空：合并之后那些格子确实是空的。
            report(
                "会丢的格子标出之后为空",
                preview.Cells.TrueForAll(c => c.AfterEmpty),
                "有格子没标成之后为空");
        }

        /// <summary>清除要列出会抹掉什么，且左上角不豁免。</summary>
        private static void TestClearListsRemovedValues(Action<string, bool, string> report)
        {
            var current = JArray.Parse(@"[[""要清的甲"",""要清的乙""]]");

            var preview = ChangePreviewBuilder.BuildDiscard(current, totalCells: 2, keepAnchor: false, kind: "clear");
            if (preview == null)
            {
                report("清除列出会抹掉的值", false, "未生成对照");
                return;
            }

            report(
                "清除把每一个有值格都算进去",
                preview.DiscardedValues == 2,
                $"discardedValues={preview.DiscardedValues}，期望 2");

            report(
                "清除不豁免左上角",
                preview.Cells.Exists(c => c.Row == 1 && c.Column == 1),
                "左上角被漏掉了");

            report("对照标明是清除", preview.Kind == "clear", preview.Kind);

            // 全空的范围：没有会丢的值，但也不是「读不到」。
            var blank = ChangePreviewBuilder.BuildDiscard(
                JArray.Parse(@"[[null,""""]]"), totalCells: 2, keepAnchor: false, kind: "clear");
            report(
                "全空范围报零而不是读不到",
                blank != null && blank.DiscardedValues == 0 && !blank.CurrentUnreadable,
                blank == null ? "未生成" : $"discarded={blank.DiscardedValues} unreadable={blank.CurrentUnreadable}");

            // 读不到当前值时如实标记，不报一个假的零。
            var unreadable = ChangePreviewBuilder.BuildDiscard(
                null, totalCells: 100, keepAnchor: false, kind: "clear");
            report(
                "读不到当前值时不报零",
                unreadable != null && unreadable.CurrentUnreadable,
                unreadable == null ? "未生成" : $"unreadable={unreadable.CurrentUnreadable}");
        }

        private static void TestPairsCurrentAgainstIncoming(Action<string, bool, string> report)
        {
            var current = JArray.Parse(@"[[""甲"",1],[""乙"",2]]");
            var incoming = JArray.Parse(@"[[""丙"",3],[""丁"",4]]");

            var preview = ChangePreviewBuilder.Build(current, incoming, totalCells: 4);

            report(
                "对照逐格配对当前值与新值",
                preview != null && preview.Cells.Count == 4,
                preview == null ? "未生成对照" : $"共 {preview.Cells.Count} 格");

            if (preview == null || preview.Cells.Count < 4)
            {
                return;
            }

            var first = preview.Cells[0];
            report(
                "第一格给出原值与新值",
                first.Before == "甲" && first.After == "丙" && first.Row == 1 && first.Column == 1,
                $"before={first.Before} after={first.After} 位置={first.Row},{first.Column}");

            // 位置用范围内的相对行列：卡片顶上已经写了绝对范围，
            // 这里再写绝对地址反而要用户自己去减。
            var last = preview.Cells[3];
            report(
                "位置是范围内的相对行列",
                last.Row == 2 && last.Column == 2,
                $"末格位置={last.Row},{last.Column}");

            report(
                "数值按字面渲染，不带本地化分隔符",
                preview.Cells[1].After == "3",
                $"after={preview.Cells[1].After}");

            report(
                "全部列出时不报省略",
                preview.OmittedCells == 0,
                $"omitted={preview.OmittedCells}");

            report(
                "读到了当前值就不标记读不到",
                !preview.CurrentUnreadable,
                "currentUnreadable 应为 false");
        }

        private static void TestEmptyIsNotUnreadable(Action<string, bool, string> report)
        {
            // 原值是空、新值是 0：这是一次正常写入，不是探测失败。
            var current = JArray.Parse(@"[[null,""""]]");
            var incoming = JArray.Parse(@"[[0,""填上""]]");

            var preview = ChangePreviewBuilder.Build(current, incoming, totalCells: 2);
            if (preview == null || preview.Cells.Count < 2)
            {
                report("空单元格与读不到分得开", false, "未生成对照");
                return;
            }

            report(
                "null 原值标记为空而非读不到",
                preview.Cells[0].BeforeEmpty && !preview.CurrentUnreadable,
                $"beforeEmpty={preview.Cells[0].BeforeEmpty} unreadable={preview.CurrentUnreadable}");

            report(
                "空字符串原值也算空",
                preview.Cells[1].BeforeEmpty,
                $"beforeEmpty={preview.Cells[1].BeforeEmpty}");

            // 0 是有值的，不能被当成空——这正是「空与 0 合成一个样子」会出的错。
            report(
                "新值 0 不算空",
                !preview.Cells[0].AfterEmpty && preview.Cells[0].After == "0",
                $"afterEmpty={preview.Cells[0].AfterEmpty} after={preview.Cells[0].After}");
        }

        private static void TestTruncation(Action<string, bool, string> report)
        {
            // 20 行 × 3 列：行方向要截到 8。
            var rows = new JArray();
            for (var r = 0; r < 20; r++)
            {
                rows.Add(new JArray($"值{r}", r, r * 2));
            }

            var preview = ChangePreviewBuilder.Build(null, rows, totalCells: 60);
            if (preview == null)
            {
                report("超出上限时按行截断", false, "未生成对照");
                return;
            }

            report(
                "行数截到上限",
                preview.Cells.Count == ChangePreviewBuilder.MaxRows * 3,
                $"列出 {preview.Cells.Count} 格，期望 {ChangePreviewBuilder.MaxRows * 3}");

            // 报格数而不是行数：截断同时发生在两个方向上，行数说不全这件事。
            report(
                "省略数按格数计算",
                preview.OmittedCells == 60 - (ChangePreviewBuilder.MaxRows * 3),
                $"omitted={preview.OmittedCells}");

            // 列方向同样要截。
            var wide = new JArray();
            var wideRow = new JArray();
            for (var c = 0; c < 12; c++)
            {
                wideRow.Add($"列{c}");
            }

            wide.Add(wideRow);
            var widePreview = ChangePreviewBuilder.Build(null, wide, totalCells: 12);
            report(
                "列数截到上限",
                widePreview != null && widePreview.Cells.Count == ChangePreviewBuilder.MaxColumns,
                widePreview == null ? "未生成对照" : $"列出 {widePreview.Cells.Count} 格");

            report(
                "列截断也报剩余格数",
                widePreview != null && widePreview.OmittedCells == 12 - ChangePreviewBuilder.MaxColumns,
                widePreview == null ? "未生成对照" : $"omitted={widePreview.OmittedCells}");

            // 单格超长文本要截，且截断本身可见。卡片上用不到工具层的 512。
            var longText = new string('长', 200);
            var single = ChangePreviewBuilder.Build(null, JArray.Parse($@"[[""{longText}""]]"), totalCells: 1);
            report(
                "单格文本截到卡片上限并带省略号",
                single != null
                    && single.Cells.Count == 1
                    && single.Cells[0].After.Length == ChangePreviewBuilder.MaxCellText + 1
                    && single.Cells[0].After.EndsWith("…", StringComparison.Ordinal),
                single == null ? "未生成对照" : $"长度={single.Cells[0].After.Length}");
        }

        private static void TestMissingCurrentMarksUnreadable(Action<string, bool, string> report)
        {
            var incoming = JArray.Parse(@"[[""新值""]]");

            // 探测失败时当前值整片缺席：必须标记读不到，
            // 而不是拿一张看起来读过的空表交给用户。
            var preview = ChangePreviewBuilder.Build(null, incoming, totalCells: 1);
            report(
                "当前值缺席时标记读不到",
                preview != null && preview.CurrentUnreadable,
                preview == null ? "未生成对照" : $"unreadable={preview.CurrentUnreadable}");

            // 没有新值就没有两边可对照，不生成对照表。
            report(
                "没有新值时不生成对照",
                ChangePreviewBuilder.Build(JArray.Parse(@"[[1]]"), null, totalCells: 1) == null,
                "incoming 为 null 应返回 null");
        }
    }
}
