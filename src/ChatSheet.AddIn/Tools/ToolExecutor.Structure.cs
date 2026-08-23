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
