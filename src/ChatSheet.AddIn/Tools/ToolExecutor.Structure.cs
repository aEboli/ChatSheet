using System;
using System.Collections.Generic;
using System.Globalization;
using ChatSheet.AddIn.Hosts;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Tools
{
    internal sealed partial class ToolExecutor
    {
        private ToolResult SetNumberFormat(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");
            var code = RequireString(args, "format_code");

            using (var range = _resolver.Resolve(address, sheet))
            {
                RangeResolver.AssertCellLimit(range, ToolLimits.MaxFormatCells, "设置数字格式");
                Com.Set(range.Range, "NumberFormatLocal", code);

                // 读回实际生效的格式：宿主可能对格式代码做本地化改写或拒绝无效代码。
                var actual = Com.GetString(range.Range, "NumberFormatLocal");
                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["sheet"] = range.SheetName,
                    ["address"] = range.Address,
                    ["cells_affected"] = range.CellCount,
                    ["requested_format"] = code,
                    ["actual_format"] = actual,
                });
            }
        }

        private ToolResult AutofitRange(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");
            var target = RequireString(args, "target").ToLowerInvariant();

            if (target != "columns" && target != "rows")
            {
                throw new ToolException("ARG_INVALID", $"target 只支持 columns 或 rows，收到「{target}」。");
            }

            using (var range = _resolver.Resolve(address, sheet))
            {
                var dimensions = target == "columns" ? range.Columns : range.Rows;
                if (dimensions > ToolLimits.MaxAutofitDimensions)
                {
                    throw new ToolException(
                        "RANGE_TOO_LARGE",
                        $"自动调整涉及 {dimensions} 个{(target == "columns" ? "列" : "行")}，超过上限 {ToolLimits.MaxAutofitDimensions}。");
                }

                object collection = null;
                try
                {
                    collection = Com.Get(range.Range, target == "columns" ? "EntireColumn" : "EntireRow");
                    Com.Call(collection, "AutoFit");
                }
                finally
                {
                    Com.Release(collection);
                }

                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["sheet"] = range.SheetName,
                    ["address"] = range.Address,
                    ["target"] = target,
                    ["dimensions_adjusted"] = dimensions,
                });
            }
        }

        /// <summary>
        /// 一次完成「适配」：居中对齐加行高列宽自动调整。
        ///
        /// 合成一个工具而不是让调用方连发三次（format_range + 两次 autofit_range）：
        /// 三次调用会留下三条撤销记录，用户点一次却要撤三次才回到原状。
        /// 这里作为单条记录登记，快照同时覆盖格式与尺寸两个维度。
        ///
        /// 顺序不能调换：对齐会改变文本的排布，进而影响自动调整算出的行高；
        /// 列宽先于行高，因为列变窄后换行的文本需要更高的行才放得下。
        ///
        /// 水平方向可选 left/center/right，默认 center；垂直方向固定居中——
        /// 「适配」要解决的是行变高后文字贴顶，垂直居中是唯一合理答案，
        /// 给它开选项只会多一个没人会改的旋钮。
        /// </summary>
        private ToolResult FitRange(JObject args)
        {
            var sheet = OptionalString(args, "sheet");
            // range 省略时取该表的已用范围：面板按钮就是这么调的，
            // 用户点「适配」要的是把整页排好，而不是先手动选一片。
            var address = OptionalString(args, "range") ?? UsedRangeAddress(sheet);
            var alignmentName = OptionalString(args, "horizontal_alignment") ?? "center";
            var alignment = ParseAlignment(alignmentName);

            using (var range = _resolver.Resolve(address, sheet))
            {
                // 刻意不设单元格上限。上限的两个理由在这里都不成立：
                // 读取受限是怕结果撑爆上下文，而适配不回传数据；
                // 写入受限是怕误伤范围过大难恢复，而适配只动对齐与行列尺寸，
                // 且留有撤销记录。剩下的唯一成本是 COM 耗时，由面板侧放宽超时承担。
                object columns = null;
                object rows = null;
                try
                {
                    Com.Set(range.Range, "HorizontalAlignment", alignment);
                    Com.Set(range.Range, "VerticalAlignment", -4108);   // xlCenter

                    columns = Com.Get(range.Range, "EntireColumn");
                    Com.Call(columns, "AutoFit");

                    rows = Com.Get(range.Range, "EntireRow");
                    Com.Call(rows, "AutoFit");
                }
                finally
                {
                    Com.Release(rows);
                    Com.Release(columns);
                }

                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["sheet"] = range.SheetName,
                    ["address"] = range.Address,
                    ["cells_affected"] = range.CellCount,
                    ["rows_adjusted"] = range.Rows,
                    ["columns_adjusted"] = range.Columns,
                    ["horizontal_alignment"] = alignmentName.ToLowerInvariant(),
                    ["applied"] = new[] { "horizontal_alignment", "vertical_alignment", "column_width", "row_height" },
                });
            }
        }

        /// <summary>
        /// 取工作表的已用范围地址。sheet 为空时用活动表。
        ///
        /// 空表时宿主的 UsedRange 仍返回 A1，无法只靠地址区分「一格数据」
        /// 和「完全空表」，因此额外读一次 CountLarge 来判断。
        /// </summary>
        private string UsedRangeAddress(string sheetName)
        {
            object worksheet = null;
            object used = null;
            try
            {
                worksheet = _resolver.ResolveWorksheet(sheetName);
                if (!Com.TryGet(worksheet, "UsedRange", out used) || used == null)
                {
                    throw new ToolException("NO_DATA", "当前工作表没有可适配的内容。");
                }

                var count = Convert.ToDouble(Com.Get(used, "CountLarge"), CultureInfo.InvariantCulture);
                var address = Com.GetString(used, "Address");

                if (count <= 1)
                {
                    object cells = null;
                    try
                    {
                        // 单格已用范围可能是真有一格数据，也可能是空表。读值区分。
                        cells = Com.Get(used, "Value2");
                        if (cells == null || string.IsNullOrEmpty(Convert.ToString(cells, CultureInfo.InvariantCulture)))
                        {
                            throw new ToolException("NO_DATA", "当前工作表没有可适配的内容。");
                        }
                    }
                    finally
                    {
                        Com.Release(cells);
                    }
                }

                return address;
            }
            finally
            {
                Com.Release(used);
                Com.Release(worksheet);
            }
        }

        private ToolResult ClearRange(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");
            var scope = RequireString(args, "scope").ToLowerInvariant();

            using (var range = _resolver.Resolve(address, sheet))
            {
                RangeResolver.AssertCellLimit(range, ToolLimits.MaxClearCells, "清除");

                switch (scope)
                {
                    case "contents":
                        Com.Call(range.Range, "ClearContents");
                        break;
                    case "formats":
                        Com.Call(range.Range, "ClearFormats");
                        break;
                    case "all":
                        Com.Call(range.Range, "Clear");
                        break;
                    default:
                        throw new ToolException("ARG_INVALID", $"scope 只支持 contents、formats、all，收到「{scope}」。");
                }

                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["sheet"] = range.SheetName,
                    ["address"] = range.Address,
                    ["cells_affected"] = range.CellCount,
                    ["scope"] = scope,
                });
            }
        }

        private ToolResult AddWorksheet(JObject args)
        {
            var name = RequireString(args, "name");
            var afterSheet = OptionalString(args, "after_sheet");
            AssertSheetName(name);

            object workbook = null;
            object sheets = null;
            object created = null;
            object anchor = null;
            try
            {
                workbook = Com.Get(Application, "ActiveWorkbook")
                    ?? throw new ToolException("NO_WORKBOOK", "当前没有打开的工作簿。");
                sheets = Com.Get(workbook, "Worksheets");

                if (SheetExists(sheets, name))
                {
                    throw new ToolException("SHEET_EXISTS", $"工作表「{name}」已存在。");
                }

                if (afterSheet != null)
                {
                    anchor = _resolver.ResolveWorksheet(afterSheet);
                    created = Com.Call(sheets, "Add", Type.Missing, anchor);
                }
                else
                {
                    // 不指定位置时置于最后一张之后，符合用户直觉。
                    var count = Convert.ToInt32(Com.Get(sheets, "Count"), CultureInfo.InvariantCulture);
                    anchor = Com.Get(sheets, "Item", count);
                    created = Com.Call(sheets, "Add", Type.Missing, anchor);
                }

                Com.Set(created, "Name", name);
                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["created_sheet"] = Com.GetString(created, "Name"),
                });
            }
            finally
            {
                Com.Release(anchor);
                Com.Release(created);
                Com.Release(sheets);
                Com.Release(workbook);
            }
        }

        private ToolResult RenameWorksheet(JObject args)
        {
            var oldName = RequireString(args, "old_name");
            var newName = RequireString(args, "new_name");
            AssertSheetName(newName);

            object sheet = null;
            object workbook = null;
            object sheets = null;
            try
            {
                workbook = Com.Get(Application, "ActiveWorkbook")
                    ?? throw new ToolException("NO_WORKBOOK", "当前没有打开的工作簿。");
                sheets = Com.Get(workbook, "Worksheets");

                if (!string.Equals(oldName, newName, StringComparison.Ordinal) && SheetExists(sheets, newName))
                {
                    throw new ToolException("SHEET_EXISTS", $"工作表「{newName}」已存在。");
                }

                sheet = _resolver.ResolveWorksheet(oldName);
                Com.Set(sheet, "Name", newName);

                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["old_name"] = oldName,
                    ["new_name"] = Com.GetString(sheet, "Name"),
                });
            }
            finally
            {
                Com.Release(sheet);
                Com.Release(sheets);
                Com.Release(workbook);
            }
        }

        private static void AssertSheetName(string name)
        {
            if (name.Length > ToolLimits.MaxSheetNameLength)
            {
                throw new ToolException(
                    "NAME_TOO_LONG",
                    $"工作表名称最长 {ToolLimits.MaxSheetNameLength} 个字符，收到 {name.Length} 个。");
            }

            // 这些字符是宿主明确禁止的，提前拦截可给出更清晰的原因。
            foreach (var ch in new[] { ':', '\\', '/', '?', '*', '[', ']' })
            {
                if (name.IndexOf(ch) >= 0)
                {
                    throw new ToolException("NAME_INVALID", $"工作表名称不能包含字符 {ch}。");
                }
            }
        }

        private static bool SheetExists(object sheets, string name)
        {
            var count = Convert.ToInt32(Com.Get(sheets, "Count"), CultureInfo.InvariantCulture);
            for (var i = 1; i <= count; i++)
            {
                object sheet = null;
                try
                {
                    sheet = Com.Get(sheets, "Item", i);
                    if (string.Equals(Com.GetString(sheet, "Name"), name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                finally
                {
                    Com.Release(sheet);
                }
            }

            return false;
        }
    }
}
