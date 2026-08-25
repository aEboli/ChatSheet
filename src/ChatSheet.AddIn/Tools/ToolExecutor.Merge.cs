using System;
using System.Collections.Generic;
using ChatSheet.AddIn.Hosts;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Tools
{
    internal sealed partial class ToolExecutor
    {
        /// <summary>
        /// 合并单元格。
        ///
        /// 这是唯一会静默丢数据的写操作：宿主只保留左上角单元格的值，
        /// 其余一概丢弃，且不留任何痕迹。因此这里做两件别处不做的事——
        /// 合并前读一遍范围，把将被丢弃的值的个数如实回报给模型；
        /// 以及关掉宿主的确认对话框，因为它会阻塞在 UI 线程上等一个
        /// 无人可点的按钮（面板的审批已经替它问过用户了）。
        ///
        /// 对齐参数是可选的：Excel 功能区上「合并后居中」是一个按钮，
        /// 用户说「合并」时想要的往往就是那个效果。分成两次调用会留下
        /// 两条撤销记录，用户点一次却要撤两次。但默认不改对齐——
        /// 已经排好版的表不该因为合并一个标题就被重新对齐。
        /// </summary>
        private ToolResult MergeCells(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");
            var across = OptionalBool(args, "across") ?? false;
            var horizontalName = OptionalString(args, "horizontal_alignment");
            var verticalName = OptionalString(args, "vertical_alignment");

            // 对齐值先解析后执行：非法值应在动手前就被拒绝，
            // 否则合并已经生效、丢掉的值也回不来了。
            var horizontal = horizontalName == null ? (int?)null : ParseAlignment(horizontalName);
            var vertical = verticalName == null ? (int?)null : ParseVerticalAlignment(verticalName);

            using (var range = _resolver.Resolve(address, sheet))
            {
                RangeResolver.AssertCellLimit(range, ToolLimits.MaxMergeCells, "合并");

                if (range.CellCount < 2)
                {
                    throw new ToolException(
                        "NOTHING_TO_MERGE",
                        $"范围 {range.Address} 只有一个单元格，无需合并。请给出跨行或跨列的范围。");
                }

                if (across && range.Columns < 2)
                {
                    throw new ToolException(
                        "NOTHING_TO_MERGE",
                        $"across 为真表示逐行合并，但范围 {range.Address} 只有一列，每行都无从合并。" +
                        "请改用多列范围，或把 across 设为假以跨行合并。");
                }

                // 合并前统计将被丢弃的值：整片读一次即可，比逐格问宿主快得多。
                var discarded = CountDiscardedValues(range, across);

                var applied = new List<string> { "merge" };
                WithoutDisplayAlerts(() => Com.Call(range.Range, "Merge", across));

                if (horizontal.HasValue)
                {
                    Com.Set(range.Range, "HorizontalAlignment", horizontal.Value);
                    applied.Add("horizontal_alignment");
                }

                if (vertical.HasValue)
                {
                    Com.Set(range.Range, "VerticalAlignment", vertical.Value);
                    applied.Add("vertical_alignment");
                }

                // 读回实际合并出的区域数：宿主可能把范围外已有的合并一并吞进来，
                // 只报告「已合并」会让模型以为版面就是它请求的样子。
                var areas = SnapshotCapture.ReadMergeAreas(range);

                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["sheet"] = range.SheetName,
                    ["address"] = range.Address,
                    ["cells_affected"] = range.CellCount,
                    ["across"] = across,
                    ["merged_areas"] = areas.Count,
                    ["areas"] = areas,
                    ["discarded_values"] = discarded,
                    ["applied"] = applied,
                });
            }
        }

        /// <summary>
        /// 取消合并。
        ///
        /// 与合并不同，这个方向不丢数据：宿主把原内容留在左上角单元格，
        /// 其余本来就是空的。因此不需要内容快照，只需记下原有的合并区域。
        /// </summary>
        private ToolResult UnmergeCells(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");

            using (var range = _resolver.Resolve(address, sheet))
            {
                RangeResolver.AssertCellLimit(range, ToolLimits.MaxMergeCells, "取消合并");

                var areas = SnapshotCapture.ReadMergeAreas(range);
                if (areas.Count == 0)
                {
                    // 当作错误而不是「成功但什么也没做」：后者会留下一条撤不出任何
                    // 变化的撤销记录，也会让模型以为版面已按它的意图改过。
                    throw new ToolException(
                        "NO_MERGED_CELLS",
                        $"范围 {range.Address} 内没有合并的单元格。");
                }

                WithoutDisplayAlerts(() => Com.Call(range.Range, "UnMerge"));

                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["sheet"] = range.SheetName,
                    ["address"] = range.Address,
                    ["cells_affected"] = range.CellCount,
                    ["areas_unmerged"] = areas.Count,
                    ["areas"] = areas,
                });
            }
        }

        /// <summary>
        /// 数一数合并会丢掉几个值。
        ///
        /// 只数非锚点位置上的非空单元格：整片合并时锚点是左上角一格，
        /// 逐行合并时每行的首格都是锚点。这个数字是模型判断「该不该先问用户」
        /// 的唯一依据，因为丢值之后没有任何迹象可查。
        /// </summary>
        private static int CountDiscardedValues(ResolvedRange range, bool across)
        {
            try
            {
                var values = ReadMatrix(range, "Value2");
                var count = 0;
                for (var r = 0; r < values.Count; r++)
                {
                    var row = values[r];
                    for (var c = 0; c < row.Count; c++)
                    {
                        // 锚点保留，不计入丢弃。
                        var isAnchor = across ? c == 0 : r == 0 && c == 0;
                        if (isAnchor)
                        {
                            continue;
                        }

                        var value = row[c];
                        if (value != null && !(value is string text && text.Length == 0))
                        {
                            count++;
                        }
                    }
                }

                return count;
            }
            catch (Exception ex)
            {
                // 数不出来不该拦住合并本身，但要让日志留下线索。
                Log.Warn("统计合并丢弃值失败：" + ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// 在关闭宿主确认对话框的状态下执行动作。
        ///
        /// 合并含多值的范围时 Excel 会弹「仅保留左上角的值」的确认框。
        /// 加载项在宿主 UI 线程上跑，弹框会把整个 Excel 连同面板一起冻住，
        /// 而那个框没有人能点——用户的许可已经由面板的审批卡收过一次。
        /// </summary>
        private void WithoutDisplayAlerts(Action action)
        {
            var app = Application;
            var hasPrevious = Com.TryGet(app, "DisplayAlerts", out var previous);
            try
            {
                Com.Set(app, "DisplayAlerts", false);
                action();
            }
            finally
            {
                if (hasPrevious && previous != null)
                {
                    try
                    {
                        Com.Set(app, "DisplayAlerts", previous);
                    }
                    catch (Exception ex)
                    {
                        // 恢复失败只影响后续弹框行为，不该盖掉操作本身的结果。
                        Log.Warn("恢复 DisplayAlerts 失败：" + ex.Message);
                    }
                }
            }
        }
    }
}
