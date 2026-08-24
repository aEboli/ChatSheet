using System;
using System.Globalization;
using ChatSheet.AddIn.Hosts;

namespace ChatSheet.AddIn.Tools
{
    /// <summary>
    /// 范围快照的采集与还原。
    ///
    /// 为什么自建撤销而不用宿主的 Undo：加载项通过对象模型所做的修改
    /// 通常不会进入 Excel 的撤销栈，甚至会清空它，所以 Ctrl+Z 对我们的
    /// 写入无效。要给用户可靠的撤销，只能自己存快照。
    /// </summary>
    internal static class SnapshotCapture
    {
        /// <summary>采集范围快照。detail 决定采集哪些维度，避免无谓开销。</summary>
        internal static RangeSnapshot Capture(ResolvedRange range, SnapshotDetail detail)
        {
            var snapshot = new RangeSnapshot
            {
                SheetName = range.SheetName,
                Address = range.Address,
                Rows = range.Rows,
                Columns = range.Columns,
            };

            if ((detail & SnapshotDetail.Content) != 0)
            {
                snapshot.Formulas = ReadMatrix(range.Range, "Formula", range.Rows, range.Columns);
                snapshot.NumberFormats = ReadMatrix(range.Range, "NumberFormatLocal", range.Rows, range.Columns);
            }

            if ((detail & SnapshotDetail.Format) != 0)
            {
                snapshot.Format = CaptureFormat(range.Range);
                // 清除格式会连带数字格式，因此格式类快照也要带上它。
                if (snapshot.NumberFormats == null)
                {
                    snapshot.NumberFormats = ReadMatrix(range.Range, "NumberFormatLocal", range.Rows, range.Columns);
                }
            }
            else if ((detail & SnapshotDetail.Alignment) != 0)
            {
                // 同样是范围级的一次读取，但不碰数字格式矩阵。
                snapshot.Format = CaptureFormat(range.Range);
            }

            if ((detail & SnapshotDetail.Size) != 0)
            {
                snapshot.ColumnWidths = ReadColumnWidths(range);
                snapshot.RowHeights = ReadRowHeights(range);
            }

            return snapshot;
        }

        /// <summary>
        /// 按快照还原。
        /// 逐项检查是否有数据再写，缺失的维度跳过而不是写入默认值。
        /// </summary>
        internal static void Restore(RangeResolver resolver, RangeSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ToolException("SNAPSHOT_MISSING", "没有可还原的快照。");
            }

            using (var range = resolver.Resolve(snapshot.Address, snapshot.SheetName))
            {
                // 范围尺寸变了说明工作簿结构已被改动，写回会错位。
                if (range.Rows != snapshot.Rows || range.Columns != snapshot.Columns)
                {
                    throw new ToolException(
                        "SHAPE_CHANGED",
                        $"范围 {snapshot.Address} 的尺寸已从 {snapshot.Rows}×{snapshot.Columns} " +
                        $"变为 {range.Rows}×{range.Columns}，无法安全还原。");
                }

                if (snapshot.NumberFormats != null)
                {
                    // 先还原数字格式：它会影响值的解释方式（例如日期）。
                    //
                    // 但失败不能拖垮整个撤销：数字格式没还原上，最坏是日期显示成
                    // 序列号，用户自己能改回来；公式没还原上，数据就真丢了。
                    // 因此这一步尽力而为，把机会留给下面的内容还原。
                    try
                    {
                        WriteMatrix(range, "NumberFormatLocal", snapshot.NumberFormats);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("还原数字格式失败，继续还原内容：" + ex.Message);
                    }
                }

                if (snapshot.Formulas != null)
                {
                    WriteMatrix(range, "Formula", snapshot.Formulas);
                }

                if (snapshot.Format != null)
                {
                    RestoreFormat(range.Range, snapshot.Format);
                }

                if (snapshot.ColumnWidths != null)
                {
                    RestoreColumnWidths(range, snapshot.ColumnWidths);
                }

                if (snapshot.RowHeights != null)
                {
                    RestoreRowHeights(range, snapshot.RowHeights);
                }
            }
        }

        /// <summary>
        /// 写回属性矩阵。
        ///
        /// 矩阵里可能残留 null（某格采集失败时留下的空位），而整片写入只要含
        /// 一个 null 就会被宿主整体拒绝。因此先看有没有空位：没有就整片写，
        /// 这是绝大多数情况且只需一次 COM 调用；有则逐格写并跳过空位，
        /// 让其余单元格照常还原，而不是因为一格没采到就全部放弃。
        /// </summary>
        private static void WriteMatrix(ResolvedRange range, string property, object[,] matrix)
        {
            if (!HasMissing(matrix))
            {
                Com.Set(range.Range, property, matrix);
                return;
            }

            Log.Warn($"{property} 快照存在未采集到的单元格，改为逐格还原并跳过这些格。");

            object cells = null;
            try
            {
                cells = Com.Get(range.Range, "Cells");
                for (var r = 0; r < range.Rows; r++)
                {
                    for (var c = 0; c < range.Columns; c++)
                    {
                        var value = matrix[r, c];
                        if (value == null || value is DBNull)
                        {
                            continue;
                        }

                        object cell = null;
                        try
                        {
                            cell = Com.Get(cells, "Item", r + 1, c + 1);
                            Com.Set(cell, property, value);
                        }
                        finally
                        {
                            Com.Release(cell);
                        }
                    }
                }
            }
            finally
            {
                Com.Release(cells);
            }
        }

        private static bool HasMissing(object[,] matrix)
        {
            foreach (var value in matrix)
            {
                if (value == null || value is DBNull)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 采集属性矩阵。
        ///
        /// 宿主对范围级属性有三种返回形态，必须分别处理：
        /// 二维数组（常规多格范围）、标量（单格范围）、Null（多格但属性不统一，
        /// 例如标题行为文本而数据行为日期时的 NumberFormatLocal）。
        /// 第三种是曾经的缺陷来源：早先把它当标量铺满整片，
        /// 于是快照变成一矩阵 null，还原时写回就抛 DISP_E_TYPEMISMATCH，
        /// 导致整个撤销中止、用户数据留在被覆盖的状态。
        /// 因此这里改为逐格读取拿真实值，慢一些但撤销必须可靠。
        /// </summary>
        private static object[,] ReadMatrix(object range, string property, int rows, int columns)
        {
            var raw = Com.Get(range, property);
            var result = new object[rows, columns];

            if (raw is Array array && array.Rank == 2)
            {
                var lowerRow = array.GetLowerBound(0);
                var lowerCol = array.GetLowerBound(1);
                for (var r = 0; r < rows; r++)
                {
                    for (var c = 0; c < columns; c++)
                    {
                        result[r, c] = array.GetValue(lowerRow + r, lowerCol + c);
                    }
                }

                return result;
            }

            // 单格范围本就返回标量，直接采用。
            if (rows * columns == 1)
            {
                result[0, 0] = raw;
                return result;
            }

            ReadMatrixCellByCell(range, property, rows, columns, result);
            return result;
        }

        /// <summary>
        /// 逐格采集。属性在范围内不统一时唯一可靠的取值方式。
        /// 单格失败不放弃整片：留下 null 会让该格在还原时被跳过，
        /// 比因为一格取不到就丢掉整个快照要好。
        /// </summary>
        private static void ReadMatrixCellByCell(
            object range,
            string property,
            int rows,
            int columns,
            object[,] result)
        {
            object cells = null;
            try
            {
                cells = Com.Get(range, "Cells");
                for (var r = 0; r < rows; r++)
                {
                    for (var c = 0; c < columns; c++)
                    {
                        object cell = null;
                        try
                        {
                            cell = Com.Get(cells, "Item", r + 1, c + 1);
                            result[r, c] = Com.Get(cell, property);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"逐格采集 {property} 失败（第 {r + 1} 行第 {c + 1} 列）：{ex.Message}");
                        }
                        finally
                        {
                            Com.Release(cell);
                        }
                    }
                }
            }
            finally
            {
                Com.Release(cells);
            }
        }

        private static FormatSnapshot CaptureFormat(object range)
        {
            object font = null;
            object interior = null;
            try
            {
                font = Com.Get(range, "Font");
                interior = Com.Get(range, "Interior");

                return new FormatSnapshot
                {
                    // 范围内属性不一致时宿主返回 null，如实记录。
                    Bold = TryRead(font, "Bold"),
                    Italic = TryRead(font, "Italic"),
                    FontSize = TryRead(font, "Size"),
                    FontColor = TryRead(font, "Color"),
                    InteriorColor = TryRead(interior, "Color"),
                    InteriorPattern = TryRead(interior, "Pattern"),
                    HorizontalAlignment = TryRead(range, "HorizontalAlignment"),
                    VerticalAlignment = TryRead(range, "VerticalAlignment"),
                    WrapText = TryRead(range, "WrapText"),
                };
            }
            finally
            {
                Com.Release(interior);
                Com.Release(font);
            }
        }

        private static void RestoreFormat(object range, FormatSnapshot format)
        {
            object font = null;
            object interior = null;
            try
            {
                font = Com.Get(range, "Font");
                interior = Com.Get(range, "Interior");

                TryWrite(font, "Bold", format.Bold);
                TryWrite(font, "Italic", format.Italic);
                TryWrite(font, "Size", format.FontSize);
                TryWrite(font, "Color", format.FontColor);
                // 先还原填充图案再还原颜色：无填充时图案为 xlNone，
                // 若只写颜色会把「无填充」变成实心填充。
                TryWrite(interior, "Pattern", format.InteriorPattern);
                TryWrite(interior, "Color", format.InteriorColor);
                TryWrite(range, "HorizontalAlignment", format.HorizontalAlignment);
                TryWrite(range, "VerticalAlignment", format.VerticalAlignment);
                TryWrite(range, "WrapText", format.WrapText);
            }
            finally
            {
                Com.Release(interior);
                Com.Release(font);
            }
        }

        private static object TryRead(object target, string name)
        {
            return Com.TryGet(target, name, out var value) ? value : null;
        }

        private static void TryWrite(object target, string name, object value)
        {
            // null 表示原范围内该属性并不统一，跳过比猜一个值更安全。
            if (value == null || value is DBNull)
            {
                return;
            }

            try
            {
                Com.Set(target, name, value);
            }
            catch (Exception ex)
            {
                Log.Warn($"还原属性 {name} 失败：{ex.Message}");
            }
        }

        private static double[] ReadColumnWidths(ResolvedRange range)
        {
            object columns = null;
            try
            {
                columns = Com.Get(range.Range, "Columns");
                var widths = new double[range.Columns];
                for (var i = 0; i < range.Columns; i++)
                {
                    object column = null;
                    try
                    {
                        column = Com.Get(columns, "Item", i + 1);
                        widths[i] = Convert.ToDouble(Com.Get(column, "ColumnWidth"), CultureInfo.InvariantCulture);
                    }
                    finally
                    {
                        Com.Release(column);
                    }
                }

                return widths;
            }
            finally
            {
                Com.Release(columns);
            }
        }

        private static double[] ReadRowHeights(ResolvedRange range)
        {
            object rows = null;
            try
            {
                rows = Com.Get(range.Range, "Rows");
                var heights = new double[range.Rows];
                for (var i = 0; i < range.Rows; i++)
                {
                    object row = null;
                    try
                    {
                        row = Com.Get(rows, "Item", i + 1);
                        heights[i] = Convert.ToDouble(Com.Get(row, "RowHeight"), CultureInfo.InvariantCulture);
                    }
                    finally
                    {
                        Com.Release(row);
                    }
                }

                return heights;
            }
            finally
            {
                Com.Release(rows);
            }
        }

        private static void RestoreColumnWidths(ResolvedRange range, double[] widths)
        {
            object columns = null;
            try
            {
                columns = Com.Get(range.Range, "Columns");
                for (var i = 0; i < widths.Length && i < range.Columns; i++)
                {
                    object column = null;
                    try
                    {
                        column = Com.Get(columns, "Item", i + 1);
                        Com.Set(column, "ColumnWidth", widths[i]);
                    }
                    finally
                    {
                        Com.Release(column);
                    }
                }
            }
            finally
            {
                Com.Release(columns);
            }
        }

        private static void RestoreRowHeights(ResolvedRange range, double[] heights)
        {
            object rows = null;
            try
            {
                rows = Com.Get(range.Range, "Rows");
                for (var i = 0; i < heights.Length && i < range.Rows; i++)
                {
                    object row = null;
                    try
                    {
                        row = Com.Get(rows, "Item", i + 1);
                        Com.Set(row, "RowHeight", heights[i]);
                    }
                    finally
                    {
                        Com.Release(row);
                    }
                }
            }
            finally
            {
                Com.Release(rows);
            }
        }
    }

    /// <summary>快照采集的维度。按需采集以免无谓开销。</summary>
    [Flags]
    internal enum SnapshotDetail
    {
        None = 0,

        /// <summary>公式与数字格式。</summary>
        Content = 1,

        /// <summary>字体、填充、对齐。</summary>
        Format = 2,

        /// <summary>列宽与行高。</summary>
        Size = 4,

        /// <summary>
        /// 只采集范围级的对齐与字体填充，不读逐格的数字格式矩阵。
        ///
        /// 与 <see cref="Format"/> 的差别只在成本：Format 会附带读一整片
        /// NumberFormatLocal（O(单元格)），适配类操作根本不改数字格式，
        /// 这片读取纯属浪费，且是整个快照里唯一随单元格数增长的部分。
        /// 去掉它之后快照成本降到 O(行+列)，几万行的表也只是几千个 double。
        /// </summary>
        Alignment = 8,

        All = Content | Format | Size,
    }
}
