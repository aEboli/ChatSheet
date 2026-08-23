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
                    Com.Set(range.Range, "NumberFormatLocal", snapshot.NumberFormats);
                }

                if (snapshot.Formulas != null)
                {
                    Com.Set(range.Range, "Formula", snapshot.Formulas);
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

            // 单元格数为一时宿主返回标量。
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    result[r, c] = raw;
                }
            }

            return result;
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

        All = Content | Format | Size,
    }
}
