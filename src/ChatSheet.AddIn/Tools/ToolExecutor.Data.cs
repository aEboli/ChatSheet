using System;
using System.Collections.Generic;
using System.Globalization;
using ChatSheet.AddIn.Hosts;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Tools
{
    internal sealed partial class ToolExecutor
    {
        private ToolResult SortRange(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");
            var keyColumn = RequireString(args, "key_column").ToUpperInvariant();
            var ascending = OptionalBool(args, "ascending") ?? true;
            var hasHeader = OptionalBool(args, "has_header") ?? true;

            using (var range = _resolver.Resolve(address, sheet))
            {
                RangeResolver.AssertCellLimit(range, ToolLimits.MaxSortCells, "排序");

                object keyRange = null;
                try
                {
                    // 排序键必须落在范围内，否则宿主会抛出难以理解的错误。
                    keyRange = ResolveKeyColumn(range, keyColumn);

                    Com.Call(
                        range.Range,
                        "Sort",
                        keyRange,
                        ascending ? 1 : 2,        // xlAscending / xlDescending
                        Type.Missing, Type.Missing, Type.Missing,
                        Type.Missing, Type.Missing,
                        hasHeader ? 1 : 2);       // xlYes / xlNo

                    return ToolResult.Success(new Dictionary<string, object>
                    {
                        ["sheet"] = range.SheetName,
                        ["address"] = range.Address,
                        ["key_column"] = keyColumn,
                        ["ascending"] = ascending,
                        ["has_header"] = hasHeader,
                        ["cells_affected"] = range.CellCount,
                    });
                }
                finally
                {
                    Com.Release(keyRange);
                }
            }
        }

        /// <summary>把列字母解析成范围内的键列，并校验它确实位于范围之内。</summary>
        private object ResolveKeyColumn(ResolvedRange range, string columnLetter)
        {
            foreach (var ch in columnLetter)
            {
                if (ch < 'A' || ch > 'Z')
                {
                    throw new ToolException("ARG_INVALID", $"key_column 应为列字母（如 B），收到「{columnLetter}」。");
                }
            }

            object column = null;
            try
            {
                column = Com.Get(range.Worksheet, "Range", columnLetter + "1");
                var columnIndex = Convert.ToInt32(Com.Get(column, "Column"), CultureInfo.InvariantCulture);

                var firstColumn = Convert.ToInt32(Com.Get(range.Range, "Column"), CultureInfo.InvariantCulture);
                var lastColumn = firstColumn + range.Columns - 1;

                if (columnIndex < firstColumn || columnIndex > lastColumn)
                {
                    throw new ToolException(
                        "KEY_OUT_OF_RANGE",
                        $"排序列 {columnLetter} 不在范围 {range.Address} 内（该范围覆盖第 {firstColumn} 到 {lastColumn} 列）。");
                }

                // 返回范围内该列对应的子范围作为排序键。
                var offset = columnIndex - firstColumn + 1;
                object cells = null;
                try
                {
                    cells = Com.Get(range.Range, "Columns");
                    return Com.Get(cells, "Item", offset);
                }
                finally
                {
                    Com.Release(cells);
                }
            }
            finally
            {
                Com.Release(column);
            }
        }

        private ToolResult CreateTable(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");
            var name = OptionalString(args, "name");
            var hasHeader = OptionalBool(args, "has_header") ?? true;

            using (var range = _resolver.Resolve(address, sheet))
            {
                object listObjects = null;
                object table = null;
                try
                {
                    listObjects = Com.Get(range.Worksheet, "ListObjects");
                    table = Com.Call(
                        listObjects,
                        "Add",
                        1,                    // xlSrcRange
                        range.Range,
                        Type.Missing,
                        hasHeader ? 1 : 2);   // xlYes / xlNo

                    if (name != null)
                    {
                        try
                        {
                            Com.Set(table, "Name", name);
                        }
                        catch (Exception ex)
                        {
                            // 表名重复或含非法字符时宿主会拒绝；表格已建成，不应整体失败。
                            Log.Warn($"设置表格名称「{name}」失败：{ex.Message}");
                        }
                    }

                    return ToolResult.Success(new Dictionary<string, object>
                    {
                        ["sheet"] = range.SheetName,
                        ["address"] = range.Address,
                        ["table_name"] = Com.GetString(table, "Name"),
                        ["has_header"] = hasHeader,
                    });
                }
                finally
                {
                    Com.Release(table);
                    Com.Release(listObjects);
                }
            }
        }

        private ToolResult CreateChart(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");
            var chartType = RequireString(args, "chart_type").ToLowerInvariant();
            var title = OptionalString(args, "title");

            var typeCode = MapChartType(chartType);

            using (var range = _resolver.Resolve(address, sheet))
            {
                object shapes = null;
                object shape = null;
                object chart = null;
                try
                {
                    shapes = Com.Get(range.Worksheet, "Shapes");

                    // 图表放在数据区右侧，避免遮挡数据。
                    var left = Convert.ToDouble(Com.Get(range.Range, "Left"), CultureInfo.InvariantCulture)
                        + Convert.ToDouble(Com.Get(range.Range, "Width"), CultureInfo.InvariantCulture) + 20;
                    var top = Convert.ToDouble(Com.Get(range.Range, "Top"), CultureInfo.InvariantCulture);

                    shape = Com.Call(shapes, "AddChart2", -1, typeCode, left, top, 420.0, 260.0);
                    chart = Com.Get(shape, "Chart");
                    Com.Call(chart, "SetSourceData", range.Range);

                    if (title != null)
                    {
                        Com.Set(chart, "HasTitle", true);
                        object titleObject = null;
                        try
                        {
                            titleObject = Com.Get(chart, "ChartTitle");
                            Com.Set(titleObject, "Text", title);
                        }
                        finally
                        {
                            Com.Release(titleObject);
                        }
                    }

                    // 回报 Shape.Name，撤销时 Shapes.Item(name) 要用的正是这个键。
                    //
                    // 刻意不用 Chart.Name：那是图表对象名，在部分宿主上与 Shape.Name
                    // 不同，拿它去 Shapes.Item 取不到。名字取不出来时留空，
                    // 撤销登记那一侧据此不承诺撤销——而不是登记一条点了必失败的记录。
                    string shapeName = null;
                    try
                    {
                        shapeName = Com.GetString(shape, "Name");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("读取图表 Shape 名称失败，本次不提供撤销：" + ex.Message);
                    }

                    return ToolResult.Success(new Dictionary<string, object>
                    {
                        ["sheet"] = range.SheetName,
                        ["source_range"] = range.Address,
                        ["chart_type"] = chartType,
                        ["title"] = title,
                        ["chart_name"] = shapeName,
                    });
                }
                finally
                {
                    Com.Release(chart);
                    Com.Release(shape);
                    Com.Release(shapes);
                }
            }
        }

        /// <summary>把易读的图表类型名映射为 XlChartType 常量。</summary>
        private static int MapChartType(string value)
        {
            switch (value)
            {
                case "column": return 51;  // xlColumnClustered
                case "bar": return 57;     // xlBarClustered
                case "line": return 4;     // xlLine
                case "pie": return 5;      // xlPie
                case "scatter": return -4169; // xlXYScatter
                case "area": return 1;     // xlArea
                default:
                    throw new ToolException(
                        "ARG_INVALID",
                        $"chart_type 只支持 column、bar、line、pie、scatter、area，收到「{value}」。");
            }
        }
    }
}
