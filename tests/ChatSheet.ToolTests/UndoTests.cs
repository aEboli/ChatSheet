using System;
using ChatSheet.AddIn.Hosts;
using ChatSheet.AddIn.Tools;
using Newtonsoft.Json.Linq;

namespace ChatSheet.ToolTests
{
    /// <summary>
    /// 撤销与恢复的真实验证。
    ///
    /// 必须跑真实 Excel：撤销的失败模式几乎全部来自 COM 的取值语义，
    /// 而这些语义无法用桩件复现。曾经漏掉的正是这一类——
    /// 范围内格式不统一时宿主返回 Null 而非矩阵，快照把整片填成 null 后
    /// 写回就抛 DISP_E_TYPEMISMATCH，撤销整体中止、用户数据留在被覆盖的状态。
    /// </summary>
    internal static class UndoTests
    {
        internal static void Run(object excel, ToolExecutor executor, Action<string, bool, string> report)
        {
            TestUndoRestoresMixedNumberFormats(excel, executor, report);
            TestUndoRestoresUniformRange(executor, report);
            TestRedoAfterUndo(executor, report);
            TestFitWithoutRangeCanUndo(excel, executor, report);
            TestMergeUndoRestoresDiscardedValues(excel, executor, report);
            TestUnmergeUndoRestoresMergedArea(excel, executor, report);
            TestMergeUndoRestoresPreexistingMerge(excel, executor, report);
        }

        /// <summary>
        /// 在已有合并的版面上再合一次，撤销要把原来那块合并装回去。
        ///
        /// 用户很少在一张干净的表上操作，更常见的是「这个标题不够宽，再往右扩一列」。
        /// 若快照不记原有的合并区域，撤销只会把整片拆平——版面回到的不是操作之前的
        /// 样子，而是一个用户从未见过的样子。
        /// </summary>
        private static void TestMergeUndoRestoresPreexistingMerge(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-merge-over-merge";

            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetCellValue(sheet, "A50", "原标题");
                SetCellValue(sheet, "C50", "会被丢的");

                // 先造出 A50:B50 这块已有的合并。
                var seed = executor.Execute("merge_cells", JObject.Parse(@"{""range"":""A50:B50""}"), null);
                if (!seed.Ok)
                {
                    report("在已有合并上再合并能撤销", false, "准备合并失败：" + seed.ErrorCode + " " + seed.Error);
                    return;
                }

                // 再把范围扩到 C50 合一次。
                var merge = executor.Execute(
                    "merge_cells",
                    JObject.Parse(@"{""range"":""A50:C50""}"),
                    undoId);

                if (!merge.Ok)
                {
                    report("在已有合并上再合并能撤销", false, merge.ErrorCode + " " + merge.Error);
                    return;
                }

                var outcome = executor.Undo.Undo(undoId);
                report(
                    "在已有合并上再合并能撤销",
                    outcome.Ok,
                    outcome.Ok ? string.Empty : outcome.ErrorCode + " " + outcome.Message);

                if (!outcome.Ok)
                {
                    return;
                }

                // 撤销后应回到「A50:B50 是一块合并，C50 独立且有值」的原状，
                // 而不是整片拆平。
                var areaAddress = MergeAreaAddress(sheet, "A50");
                var c50 = Convert.ToString(ReadCell(sheet, "C50"));
                report(
                    "撤销后原有的合并区域已装回",
                    areaAddress.IndexOf("A$50:$B$50", StringComparison.Ordinal) >= 0,
                    $"A50 所在合并区域={areaAddress}");
                report(
                    "撤销后范围外缘的值已找回",
                    c50 == "会被丢的" && !IsMerged(sheet, "C50"),
                    $"C50={c50} 合并={IsMerged(sheet, "C50")}");
            }
            catch (Exception ex)
            {
                report("在已有合并上再合并能撤销", false, "抛出异常：" + Describe(ex));
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        private static string MergeAreaAddress(object sheet, string address)
        {
            object cell = null;
            object area = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                if (!(Com.Get(cell, "MergeCells") is bool merged) || !merged)
                {
                    return "<未合并>";
                }

                area = Com.Get(cell, "MergeArea");
                return Com.GetString(area, "Address");
            }
            finally
            {
                Com.Release(area);
                Com.Release(cell);
            }
        }

        /// <summary>
        /// 合并的撤销必须把被丢弃的值找回来。
        ///
        /// 这是所有写操作里唯一会静默丢数据的一个：宿主只留左上角一格，
        /// 其余内容直接丢弃且不留痕迹。撤销若只把格子拆回来而值没回来，
        /// 用户看到的是一片空表，且没有别的办法找回——比不提供撤销更糟。
        ///
        /// 还原顺序也在这里被验证：合并区域里只有左上角可写，必须先拆平
        /// 才能整片写回，否则宿主报「要求合并单元格具有相同大小」。
        /// </summary>
        private static void TestMergeUndoRestoresDiscardedValues(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string address = "A40:C40";
            const string undoId = "undo-merge";

            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetCellValue(sheet, "A40", "留下的");
                SetCellValue(sheet, "B40", "会被丢的甲");
                SetCellValue(sheet, "C40", "会被丢的乙");
                SetAlignment(sheet, address, -4131, -4160);   // xlLeft / xlTop

                var merge = executor.Execute(
                    "merge_cells",
                    JObject.Parse(@"{""range"":""" + address + @""",""horizontal_alignment"":""center""}"),
                    undoId);

                if (!merge.Ok)
                {
                    report("合并能撤销", false, merge.ErrorCode + " " + merge.Error);
                    return;
                }

                report(
                    "合并真的生效",
                    IsMerged(sheet, "A40"),
                    "A40 未处于合并状态");

                var outcome = executor.Undo.Undo(undoId);
                report(
                    "合并能撤销",
                    outcome.Ok,
                    outcome.Ok ? string.Empty : outcome.ErrorCode + " " + outcome.Message);

                if (!outcome.Ok)
                {
                    return;
                }

                // 声称成功不够：要确认格子拆回来了、被丢的值也回来了。
                var stillMerged = IsMerged(sheet, "A40");
                var b40 = Convert.ToString(ReadCell(sheet, "B40"));
                var c40 = Convert.ToString(ReadCell(sheet, "C40"));
                report(
                    "撤销合并后拆回独立单元格",
                    !stillMerged,
                    stillMerged ? "A40 仍处于合并状态" : string.Empty);
                report(
                    "撤销合并后被丢弃的值已找回",
                    b40 == "会被丢的甲" && c40 == "会被丢的乙",
                    $"B40={b40} C40={c40}");

                // 对齐也是合并工具改的，撤销要一并回退。
                var horizontal = ReadAlignment(sheet, "A40", "HorizontalAlignment");
                report(
                    "撤销合并后对齐已回退",
                    horizontal == -4131,
                    $"A40 水平对齐={horizontal}");

                // 恢复要把合并装回去，否则用户误撤销就没有退路。
                var redone = executor.Undo.Redo(undoId);
                report(
                    "撤销合并后能恢复",
                    redone.Ok && IsMerged(sheet, "A40"),
                    redone.Ok ? "恢复后 A40 未处于合并状态" : redone.ErrorCode + " " + redone.Message);
            }
            catch (Exception ex)
            {
                report("合并能撤销", false, "抛出异常：" + Describe(ex));
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>
        /// 取消合并的撤销必须把合并区域照原样装回去。
        ///
        /// 这个方向不丢数据，但快照只记合并区域、不记内容，所以要单独验证：
        /// 记漏了的话撤销会声称成功而版面纹丝不动。
        /// </summary>
        private static void TestUnmergeUndoRestoresMergedArea(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string address = "A45:C45";
            const string undoId = "undo-unmerge";

            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                SetCellValue(sheet, "A45", "合并标题");
                var seed = executor.Execute("merge_cells", JObject.Parse(@"{""range"":""" + address + @"""}"), null);
                if (!seed.Ok)
                {
                    report("取消合并能撤销", false, "准备合并失败：" + seed.ErrorCode + " " + seed.Error);
                    return;
                }

                var unmerge = executor.Execute(
                    "unmerge_cells",
                    JObject.Parse(@"{""range"":""" + address + @"""}"),
                    undoId);

                if (!unmerge.Ok)
                {
                    report("取消合并能撤销", false, unmerge.ErrorCode + " " + unmerge.Error);
                    return;
                }

                var outcome = executor.Undo.Undo(undoId);
                var remerged = IsMerged(sheet, "A45");
                report(
                    "取消合并能撤销",
                    outcome.Ok,
                    outcome.Ok ? string.Empty : outcome.ErrorCode + " " + outcome.Message);
                report(
                    "撤销取消合并后合并区域已装回",
                    outcome.Ok && remerged,
                    remerged ? string.Empty : "A45 未恢复为合并状态");

                var redone = outcome.Ok ? executor.Undo.Redo(undoId) : null;
                report(
                    "撤销取消合并后能恢复",
                    redone != null && redone.Ok && !IsMerged(sheet, "A45"),
                    redone == null
                        ? "撤销未成功，无法验证恢复"
                        : redone.ErrorCode + " " + redone.Message);
            }
            catch (Exception ex)
            {
                report("取消合并能撤销", false, "抛出异常：" + Describe(ex));
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        private static bool IsMerged(object sheet, string address)
        {
            object cell = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                return Com.Get(cell, "MergeCells") is bool merged && merged;
            }
            finally
            {
                Com.Release(cell);
            }
        }

        /// <summary>
        /// 范围内数字格式不统一时仍能撤销。
        ///
        /// 这是 DISP_E_TYPEMISMATCH 的复现场景：给同一范围内的单元格设置不同的
        /// 数字格式，Excel 的 NumberFormatLocal 便不再返回矩阵而返回 Null。
        /// 用户侧的触发条件很常见——标题行是文本、数据行是数字或日期。
        /// </summary>
        private static void TestUndoRestoresMixedNumberFormats(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string address = "A10:B11";
            const string undoId = "undo-mixed-format";

            object sheet = null;
            try
            {
                sheet = Com.Get(excel, "ActiveSheet");

                // 造出格式不统一的范围：一格文本、一格两位小数、一格日期、一格常规。
                //
                // 用 NumberFormat 而非 NumberFormatLocal 来准备数据：后者只接受
                // 本地化的格式代码，在中文 Excel 下传 "General"、"yyyy-mm-dd"
                // 会被拒绝（报「不能设置 NumberFormatLocal 属性」）。
                // 被测代码读写的仍是 NumberFormatLocal，两者指向同一属性的不同表示。
                SetCellFormat(sheet, "A10", "@");
                SetCellFormat(sheet, "B10", "0.00");
                SetCellFormat(sheet, "A11", "yyyy-mm-dd");
                SetCellFormat(sheet, "B11", "General");

                SetCellValue(sheet, "A10", "原始甲");
                SetCellValue(sheet, "B10", 1.25);
                SetCellValue(sheet, "A11", "原始乙");
                SetCellValue(sheet, "B11", 7);

                var write = executor.Execute(
                    "write_values",
                    JObject.Parse(@"{""range"":""" + address + @""",""values"":[[""新甲"",9.5],[""新乙"",42]]}"),
                    undoId);

                if (!write.Ok)
                {
                    report("格式不统一的范围可写入", false, write.ErrorCode + " " + write.Error);
                    return;
                }

                var outcome = executor.Undo.Undo(undoId);
                report(
                    "格式不统一的范围能撤销",
                    outcome.Ok,
                    outcome.Ok ? string.Empty : outcome.ErrorCode + " " + outcome.Message);

                if (!outcome.Ok)
                {
                    return;
                }

                // 撤销声称成功还不够，必须读回单元格确认内容真的回退了。
                var a10 = Convert.ToString(ReadCell(sheet, "A10"));
                var b10 = ReadCell(sheet, "B10");
                report(
                    "撤销后内容已回退",
                    a10 == "原始甲" && Math.Abs(Convert.ToDouble(b10) - 1.25) < 0.0001,
                    $"A10={a10} B10={b10}");
            }
            catch (Exception ex)
            {
                report("格式不统一的范围能撤销", false, "抛出异常：" + Describe(ex));
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>格式统一的范围是原本就能走通的路径，防止修复时把它弄坏。</summary>
        private static void TestUndoRestoresUniformRange(ToolExecutor executor, Action<string, bool, string> report)
        {
            const string undoId = "undo-uniform";
            try
            {
                executor.Execute(
                    "write_values",
                    JObject.Parse(@"{""range"":""A20:B20"",""values"":[[""甲"",1]]}"),
                    "undo-uniform-seed");

                var write = executor.Execute(
                    "write_values",
                    JObject.Parse(@"{""range"":""A20:B20"",""values"":[[""乙"",2]]}"),
                    undoId);

                var outcome = write.Ok ? executor.Undo.Undo(undoId) : null;
                report(
                    "格式统一的范围能撤销",
                    outcome != null && outcome.Ok,
                    outcome == null ? "写入失败" : outcome.ErrorCode + " " + outcome.Message);
            }
            catch (Exception ex)
            {
                report("格式统一的范围能撤销", false, "抛出异常：" + ex.Message);
            }
        }

        /// <summary>撤销后必须能恢复，否则用户误撤销就没有退路。</summary>
        private static void TestRedoAfterUndo(ToolExecutor executor, Action<string, bool, string> report)
        {
            const string undoId = "undo-then-redo";
            try
            {
                var write = executor.Execute(
                    "write_values",
                    JObject.Parse(@"{""range"":""A30"",""values"":[[""恢复目标""]]}"),
                    undoId);

                if (!write.Ok)
                {
                    report("撤销后能恢复", false, write.ErrorCode + " " + write.Error);
                    return;
                }

                var undone = executor.Undo.Undo(undoId);
                var redone = undone.Ok ? executor.Undo.Redo(undoId) : null;

                report(
                    "撤销后能恢复",
                    redone != null && redone.Ok && !redone.Undone,
                    redone == null
                        ? "撤销未成功：" + undone.ErrorCode + " " + undone.Message
                        : redone.ErrorCode + " " + redone.Message);
            }
            catch (Exception ex)
            {
                report("撤销后能恢复", false, "抛出异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 面板的「适配」按钮省略 range，由工具自行取活动表已用范围。
        /// 撤销快照必须在操作前采集，因此执行器要先把隐式范围解析回参数。
        /// </summary>
        private static void TestFitWithoutRangeCanUndo(
            object excel,
            ToolExecutor executor,
            Action<string, bool, string> report)
        {
            const string undoId = "undo-fit-used-range";
            object sheets = null;
            object originalSheet = null;
            object sheet = null;
            object range = null;

            try
            {
                originalSheet = Com.Get(excel, "ActiveSheet");
                sheets = Com.Get(excel, "Worksheets");
                sheet = Com.Call(sheets, "Add");
                Com.Set(sheet, "Name", "适配撤销验证");
                Com.Call(sheet, "Activate");

                range = Com.Get(sheet, "Range", "A1:B2");
                Com.Set(range, "Value2", new object[,]
                {
                    { "标题一", "标题二" },
                    { "较长的内容", "值" },
                });
                SetAlignment(sheet, "A1:B1", -4108, -4108);  // xlCenter
                SetAlignment(sheet, "A2:B2", -4131, -4160);  // xlLeft / xlTop

                var args = new JObject();
                var fit = executor.Execute("fit_range", args, undoId);
                var record = executor.Undo.Find(undoId);
                var undone = fit.Ok && record != null
                    ? executor.Undo.Undo(undoId)
                    : null;

                report(
                    "省略范围的整表适配会登记撤销",
                    fit.Ok && record != null && !string.IsNullOrWhiteSpace(args.Value<string>("range")),
                    fit.Ok ? args.ToString() : fit.ErrorCode + " " + fit.Error);
                report(
                    "省略范围的整表适配可以撤销",
                    undone != null && undone.Ok,
                    undone == null ? "没有撤销记录" : undone.ErrorCode + " " + undone.Message);

                var topHorizontal = ReadAlignment(sheet, "A1", "HorizontalAlignment");
                var topVertical = ReadAlignment(sheet, "A1", "VerticalAlignment");
                var bottomHorizontal = ReadAlignment(sheet, "A2", "HorizontalAlignment");
                var bottomVertical = ReadAlignment(sheet, "A2", "VerticalAlignment");
                report(
                    "省略范围的整表适配撤销后恢复混合对齐",
                    undone != null && undone.Ok
                    && topHorizontal == -4108 && topVertical == -4108
                    && bottomHorizontal == -4131 && bottomVertical == -4160,
                    $"A1={topHorizontal}/{topVertical} A2={bottomHorizontal}/{bottomVertical}");

                // 撤销之后必须还能恢复。面板上是同一个按钮的两个方向，
                // 只验证撤销的话，「撤销能用、恢复报找不到记录」这种半残状态
                // 照样能通过测试——而用户点的正是同一个按钮。
                var redone = undone != null && undone.Ok
                    ? executor.Undo.Redo(undoId)
                    : null;

                report(
                    "省略范围的整表适配可以恢复",
                    redone != null && redone.Ok && !redone.Undone,
                    redone == null
                        ? "撤销未成功，无法验证恢复"
                        : redone.ErrorCode + " " + redone.Message);

                // 恢复要把两行都带回适配后的居中状态，而不只是声称成功。
                var redoneTopHorizontal = ReadAlignment(sheet, "A1", "HorizontalAlignment");
                var redoneBottomHorizontal = ReadAlignment(sheet, "A2", "HorizontalAlignment");
                var redoneBottomVertical = ReadAlignment(sheet, "A2", "VerticalAlignment");
                report(
                    "省略范围的整表适配恢复后重新居中",
                    redone != null && redone.Ok
                    && redoneTopHorizontal == -4108
                    && redoneBottomHorizontal == -4108 && redoneBottomVertical == -4108,
                    $"A1={redoneTopHorizontal} A2={redoneBottomHorizontal}/{redoneBottomVertical}");
            }
            catch (Exception ex)
            {
                report("省略范围的整表适配可以撤销", false, "抛出异常：" + Describe(ex));
            }
            finally
            {
                try
                {
                    if (originalSheet != null)
                    {
                        Com.Call(originalSheet, "Activate");
                    }

                    if (sheet != null)
                    {
                        Com.Call(sheet, "Delete");
                    }
                }
                catch
                {
                    // 测试结果已经记录，清理失败不覆盖原始结论。
                }

                Com.Release(range);
                Com.Release(sheet);
                Com.Release(originalSheet);
                Com.Release(sheets);
            }
        }

        /// <summary>
        /// 展开异常链并带上调用栈。
        /// 后期绑定失败时外层只会说「调用的目标发生了异常」，
        /// 真正的原因和出错位置都在内层。
        /// </summary>
        private static string Describe(Exception ex)
        {
            var text = string.Empty;
            var current = ex;
            var depth = 0;
            while (current != null && depth < 5)
            {
                text += (depth == 0 ? string.Empty : " ← ") + current.GetType().Name + ": " + current.Message;
                current = current.InnerException;
                depth++;
            }

            return text + "\n        栈：" + ex.StackTrace;
        }

        private static void SetCellFormat(object sheet, string address, string format)
        {
            object cell = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                Com.Set(cell, "NumberFormat", format);
            }
            finally
            {
                Com.Release(cell);
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

        private static void SetAlignment(object sheet, string address, int horizontal, int vertical)
        {
            object range = null;
            try
            {
                range = Com.Get(sheet, "Range", address);
                Com.Set(range, "HorizontalAlignment", horizontal);
                Com.Set(range, "VerticalAlignment", vertical);
            }
            finally
            {
                Com.Release(range);
            }
        }

        private static int ReadAlignment(object sheet, string address, string property)
        {
            object cell = null;
            try
            {
                cell = Com.Get(sheet, "Range", address);
                return Convert.ToInt32(Com.Get(cell, property));
            }
            finally
            {
                Com.Release(cell);
            }
        }
    }
}
