using System;
using System.Collections.Generic;
using System.Globalization;
using ChatSheet.AddIn.Hosts;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Tools
{
    internal sealed partial class ToolExecutor
    {
        // ---- 参数读取辅助。模型给的参数可能缺失或类型不符，一律转成可读错误。 ----

        private static string RequireString(JObject args, string name)
        {
            var value = args?.Value<string>(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ToolException("ARG_MISSING", $"缺少必需参数 {name}。");
            }

            return value.Trim();
        }

        private static string OptionalString(JObject args, string name)
        {
            var token = args?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            var value = token.Value<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool? OptionalBool(JObject args, string name)
        {
            var token = args?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            try
            {
                return token.Value<bool>();
            }
            catch
            {
                throw new ToolException("ARG_INVALID", $"参数 {name} 应为布尔值。");
            }
        }

        private static double? OptionalNumber(JObject args, string name)
        {
            var token = args?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            try
            {
                return token.Value<double>();
            }
            catch
            {
                throw new ToolException("ARG_INVALID", $"参数 {name} 应为数字。");
            }
        }

        private static JArray RequireArray(JObject args, string name)
        {
            if (!(args?[name] is JArray array) || array.Count == 0)
            {
                throw new ToolException("ARG_MISSING", $"参数 {name} 必须是非空的二维数组。");
            }

            return array;
        }

        /// <summary>
        /// 写入值或公式。
        ///
        /// 写入后统一读回并把实际内容返回给模型：宿主可能因单元格格式、
        /// 保护状态或公式错误而使写入结果与请求不一致，只报告「成功」会误导模型。
        /// </summary>
        private ToolResult WriteMatrix(JObject args, bool formulas)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");
            var key = formulas ? "formulas" : "values";
            var matrix = RequireArray(args, key);

            using (var range = _resolver.Resolve(address, sheet))
            {
                RangeResolver.AssertCellLimit(range, ToolLimits.MaxWriteCells, "写入");

                var rows = matrix.Count;
                var columns = 0;
                for (var r = 0; r < rows; r++)
                {
                    if (!(matrix[r] is JArray rowArray))
                    {
                        throw new ToolException("ARG_INVALID", $"{key} 的第 {r + 1} 行不是数组。");
                    }

                    columns = Math.Max(columns, rowArray.Count);
                }

                if (rows != range.Rows || columns != range.Columns)
                {
                    throw new ToolException(
                        "SHAPE_MISMATCH",
                        $"数据尺寸 {rows} 行 × {columns} 列与范围 {range.Address} 的 " +
                        $"{range.Rows} 行 × {range.Columns} 列不一致。请调整数据或范围使二者相符。");
                }

                var buffer = new object[rows, columns];
                for (var r = 0; r < rows; r++)
                {
                    var rowArray = (JArray)matrix[r];
                    for (var c = 0; c < columns; c++)
                    {
                        var token = c < rowArray.Count ? rowArray[c] : null;
                        buffer[r, c] = ConvertCell(token, formulas, r, c);
                    }
                }

                Com.Set(range.Range, formulas ? "Formula" : "Value2", buffer);

                // 读回校验：这是判断写入是否真正生效的唯一可靠方式。
                var readBack = ReadMatrix(range, "Value2");
                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["sheet"] = range.SheetName,
                    ["address"] = range.Address,
                    ["cells_written"] = range.CellCount,
                    ["verification"] = readBack,
                });
            }
        }

        private static object ConvertCell(JToken token, bool formulas, int row, int column)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return formulas ? (object)string.Empty : null;
            }

            if (formulas)
            {
                var text = token.Value<string>() ?? string.Empty;
                if (text.Length > 0 && !text.StartsWith("=", StringComparison.Ordinal))
                {
                    throw new ToolException(
                        "FORMULA_INVALID",
                        $"第 {row + 1} 行第 {column + 1} 列的公式「{text}」未以 = 开头。");
                }

                return text;
            }

            switch (token.Type)
            {
                case JTokenType.Integer:
                    return token.Value<long>();
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.Boolean:
                    return token.Value<bool>();
                default:
                    return token.Value<string>();
            }
        }

        private ToolResult FormatRange(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");

            using (var range = _resolver.Resolve(address, sheet))
            {
                RangeResolver.AssertCellLimit(range, ToolLimits.MaxFormatCells, "设置格式");

                var applied = new List<string>();
                object font = null;
                object interior = null;
                try
                {
                    font = Com.Get(range.Range, "Font");

                    var bold = OptionalBool(args, "bold");
                    if (bold.HasValue) { Com.Set(font, "Bold", bold.Value); applied.Add("bold"); }

                    var italic = OptionalBool(args, "italic");
                    if (italic.HasValue) { Com.Set(font, "Italic", italic.Value); applied.Add("italic"); }

                    var size = OptionalNumber(args, "font_size");
                    if (size.HasValue)
                    {
                        if (size.Value < 1 || size.Value > 409)
                        {
                            throw new ToolException("ARG_INVALID", "font_size 必须在 1 到 409 之间。");
                        }

                        Com.Set(font, "Size", size.Value);
                        applied.Add("font_size");
                    }

                    var fontColor = OptionalString(args, "font_color");
                    if (fontColor != null) { Com.Set(font, "Color", ParseColor(fontColor, "font_color")); applied.Add("font_color"); }

                    var fillColor = OptionalString(args, "fill_color");
                    if (fillColor != null)
                    {
                        interior = Com.Get(range.Range, "Interior");
                        Com.Set(interior, "Color", ParseColor(fillColor, "fill_color"));
                        applied.Add("fill_color");
                    }

                    var alignment = OptionalString(args, "horizontal_alignment");
                    if (alignment != null)
                    {
                        Com.Set(range.Range, "HorizontalAlignment", ParseAlignment(alignment));
                        applied.Add("horizontal_alignment");
                    }

                    var vertical = OptionalString(args, "vertical_alignment");
                    if (vertical != null)
                    {
                        Com.Set(range.Range, "VerticalAlignment", ParseVerticalAlignment(vertical));
                        applied.Add("vertical_alignment");
                    }

                    var wrap = OptionalBool(args, "wrap_text");
                    if (wrap.HasValue) { Com.Set(range.Range, "WrapText", wrap.Value); applied.Add("wrap_text"); }
                }
                finally
                {
                    Com.Release(interior);
                    Com.Release(font);
                }

                if (applied.Count == 0)
                {
                    throw new ToolException("NO_CHANGES", "未提供任何要修改的格式属性。");
                }

                return ToolResult.Success(new Dictionary<string, object>
                {
                    ["sheet"] = range.SheetName,
                    ["address"] = range.Address,
                    ["cells_affected"] = range.CellCount,
                    ["applied"] = applied,
                });
            }
        }

        /// <summary>把 #RRGGBB 转成 COM 需要的 BGR 整数。顺序反了会得到错误颜色。</summary>
        private static int ParseColor(string value, string argName)
        {
            var text = value.TrimStart('#');
            if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                throw new ToolException("ARG_INVALID", $"{argName} 应为 #RRGGBB 形式的十六进制颜色，收到「{value}」。");
            }

            var r = (rgb >> 16) & 0xFF;
            var g = (rgb >> 8) & 0xFF;
            var b = rgb & 0xFF;
            return (b << 16) | (g << 8) | r;
        }

        private static int ParseAlignment(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "left": return -4131;   // xlLeft
                case "center": return -4108; // xlCenter
                case "right": return -4152;  // xlRight
                default:
                    throw new ToolException("ARG_INVALID", $"horizontal_alignment 只支持 left、center、right，收到「{value}」。");
            }
        }

        /// <summary>
        /// 垂直对齐的常量与水平不同名但有重叠：xlCenter（-4108）两个方向共用，
        /// 顶部与底部则各有专属值，不能套用水平那套。
        /// </summary>
        private static int ParseVerticalAlignment(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "top": return -4160;    // xlTop
                case "center": return -4108; // xlCenter
                case "bottom": return -4107; // xlBottom
                default:
                    throw new ToolException("ARG_INVALID", $"vertical_alignment 只支持 top、center、bottom，收到「{value}」。");
            }
        }
    }
}
