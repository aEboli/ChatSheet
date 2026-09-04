using System;
using System.Collections.Generic;
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
        internal static RangeSnapshot Capture(
            ResolvedRange range,
            SnapshotDetail detail,
            bool allowCellwiseAlignment)
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

                // 范围内不一致的属性，宿主返回 null，还原时一律跳过。
                //
                // 分两档处置，因为「不统一」有两种程度：
                //
                // 全部九项都不一致 → 这条记录什么也还原不了。格式是唯一维度时
                // （format_range）放弃整条记录，与适配对混合对齐的取舍同一句话：
                // 保不住足以完整还原的快照，就不承诺可以撤销。
                //
                // 只有部分不一致 → 还原得回一部分。记录要留，但标明不完整，
                // 由卡片如实说明。若把这一档也当成「完整」，用户会拿到一个
                // 自称能撤、实际漏还原几项的按钮。
                //
                // 逐格外观矩阵不做：每格九个属性的 COM 成本远高于适配的两个对齐维度。
                if (snapshot.Format != null)
                {
                    if (snapshot.Format.IsAllNull && (detail & SnapshotDetail.Content) == 0)
                    {
                        return null;
                    }

                    // 有内容维度时（clear_range 会丢值）总是留下记录：
                    // 值找回来比格式要紧，但格式的欠账要说出来。
                    snapshot.FormatIncomplete = snapshot.Format.HasMixedProperty;
                }

                // 清除格式会连带数字格式，因此格式类快照也要带上它。
                if (snapshot.NumberFormats == null)
                {
                    snapshot.NumberFormats = ReadMatrix(range.Range, "NumberFormatLocal", range.Rows, range.Columns);
                }
            }
            else if ((detail & SnapshotDetail.Alignment) != 0)
            {
                snapshot.Alignment = CaptureAlignment(range, allowCellwiseAlignment);
                if (snapshot.Alignment == null)
                {
                    return null;
                }
            }

            if ((detail & SnapshotDetail.Size) != 0)
            {
                snapshot.ColumnWidths = ReadColumnWidths(range);
                snapshot.RowHeights = ReadRowHeights(range);
            }

            if ((detail & SnapshotDetail.Merge) != 0)
            {
                snapshot.MergeAreas = ReadMergeAreas(range);
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

                // 合并状态必须最先拆、最后装。
                //
                // 合并区域里只有左上角一格可写，向其余格写值会被宿主拒绝
                // （报「要求合并单元格具有相同大小」）。因此先把范围拆平，
                // 让整片格子重新可写，再按快照还原内容与格式，最后照原样合回去。
                var restoreMerge = snapshot.MergeAreas != null;
                if (restoreMerge)
                {
                    UnmergeAll(range);
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

                if (snapshot.Alignment != null)
                {
                    RestoreAlignment(range, snapshot.Alignment);
                }

                if (snapshot.ColumnWidths != null)
                {
                    RestoreColumnWidths(range, snapshot.ColumnWidths);
                }

                if (snapshot.RowHeights != null)
                {
                    RestoreRowHeights(range, snapshot.RowHeights);
                }

                if (restoreMerge)
                {
                    RemergeAreas(range, snapshot.MergeAreas);
                }
            }
        }

        /// <summary>
        /// 读取范围内的合并区域地址，按区域去重。
        ///
        /// 范围级 MergeCells 为 false 时可以一次断定没有合并，这是常见情形；
        /// 返回 true 或 Null（范围内不统一）时只能逐格问，因为宿主没有
        /// 「列出这片里的合并区域」这样的成员。
        ///
        /// 得到的地址可能越出原范围：跨界的合并区域必须整块记下来，
        /// 否则还原时会把它复原成半块，比不还原更糟。
        /// </summary>
        internal static List<string> ReadMergeAreas(ResolvedRange range)
        {
            var areas = new List<string>();

            if (TryRead(range.Range, "MergeCells") is bool uniform && !uniform)
            {
                return areas;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            object cells = null;
            try
            {
                cells = Com.Get(range.Range, "Cells");
                for (var r = 0; r < range.Rows; r++)
                {
                    for (var c = 0; c < range.Columns; c++)
                    {
                        object cell = null;
                        object area = null;
                        try
                        {
                            cell = Com.Get(cells, "Item", r + 1, c + 1);
                            if (!(TryRead(cell, "MergeCells") is bool merged) || !merged)
                            {
                                continue;
                            }

                            area = Com.Get(cell, "MergeArea");
                            var address = Com.GetString(area, "Address");
                            if (!string.IsNullOrEmpty(address) && seen.Add(address))
                            {
                                areas.Add(address);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"读取合并区域失败（第 {r + 1} 行第 {c + 1} 列）：{ex.Message}");
                        }
                        finally
                        {
                            Com.Release(area);
                            Com.Release(cell);
                        }
                    }
                }
            }
            finally
            {
                Com.Release(cells);
            }

            return areas;
        }

        /// <summary>把范围内所有合并区域拆平。相交的合并区域会被整块拆开。</summary>
        private static void UnmergeAll(ResolvedRange range)
        {
            WithoutDisplayAlerts(range, () => Com.Call(range.Range, "UnMerge"));
        }

        /// <summary>按快照把合并区域装回去。单块失败不放弃其余块。</summary>
        private static void RemergeAreas(ResolvedRange range, IReadOnlyList<string> areas)
        {
            if (areas.Count == 0)
            {
                return;
            }

            WithoutDisplayAlerts(range, () =>
            {
                foreach (var address in areas)
                {
                    object area = null;
                    try
                    {
                        area = Com.Get(range.Worksheet, "Range", address);
                        Com.Call(area, "Merge", false);
                    }
                    catch (Exception ex)
                    {
                        // 一块合不回去时其余块照常还原：少一处合并用户看得见也改得回来，
                        // 中途放弃则会留下半新半旧的版面。
                        Log.Warn($"还原合并区域 {address} 失败：{ex.Message}");
                    }
                    finally
                    {
                        Com.Release(area);
                    }
                }
            });
        }

        /// <summary>
        /// 关掉宿主确认对话框执行动作。
        ///
        /// 合并含多值的范围会弹确认框，而加载项跑在宿主 UI 线程上，
        /// 弹框会把 Excel 连同面板一起冻住，且没有人能点它。
        /// 这里从工作表反查 Application，避免为撤销单独持有宿主引用。
        /// </summary>
        private static void WithoutDisplayAlerts(ResolvedRange range, Action action)
        {
            object app = null;
            object previous = null;
            var restore = false;
            try
            {
                if (Com.TryGet(range.Worksheet, "Application", out app) && app != null)
                {
                    if (Com.TryGet(app, "DisplayAlerts", out previous) && previous != null)
                    {
                        restore = true;
                    }

                    try
                    {
                        Com.Set(app, "DisplayAlerts", false);
                    }
                    catch (Exception ex)
                    {
                        restore = false;
                        Log.Warn("关闭 DisplayAlerts 失败，合并还原可能弹框：" + ex.Message);
                    }
                }

                action();
            }
            finally
            {
                if (restore)
                {
                    try
                    {
                        Com.Set(app, "DisplayAlerts", previous);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("恢复 DisplayAlerts 失败：" + ex.Message);
                    }
                }

                Com.Release(app);
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
                if (IsMissing(value))
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

        /// <summary>
        /// 只为审批卡探测「当前格式统不统一」而读一次范围级外观。
        ///
        /// 与撤销采集共用同一份读取逻辑：判断标准必须与「格式能不能完整还原」
        /// 完全一致，两处各写一份迟早会分叉——那时卡片说的和撤销做的就不是一件事。
        /// </summary>
        internal static FormatSnapshot CaptureFormatForProbe(object range)
        {
            return CaptureFormat(range);
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
                    // 三项单值读取，各一次 COM。清除格式会把它们一并重置，
                    // 而它们此前完全没进快照——撤销悄悄丢掉字体与下划线。
                    FontName = TryRead(font, "Name"),
                    FontUnderline = TryRead(font, "Underline"),
                    FontStrikethrough = TryRead(font, "Strikethrough"),
                    FontColor = TryRead(font, "Color"),
                    // 只为判断上一行该不该写回而读，不参与还原。见 FormatSnapshot 的说明。
                    FontThemeColor = TryRead(font, "ThemeColor"),
                    FontTintAndShade = TryRead(font, "TintAndShade"),
                    InteriorColor = TryRead(interior, "Color"),
                    // 只为判断上一行可不可信而读，不参与还原。见 FormatSnapshot 的说明。
                    InteriorColorIndex = TryRead(interior, "ColorIndex"),
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

        /// <summary>
        /// 采集适配实际改变的两个对齐维度。
        ///
        /// 常见的大表通常统一对齐，范围级读取即可；只有原始对齐混合时才逐格读取。
        /// 若逐格快照不被允许或有任一单元格无法读取，放弃整条撤销记录，保证用户
        /// 看到撤销按钮就代表两种对齐都能完整还原。
        /// </summary>
        private static AlignmentSnapshot CaptureAlignment(
            ResolvedRange range,
            bool allowCellwiseAlignment)
        {
            var snapshot = new AlignmentSnapshot
            {
                HorizontalAlignment = TryRead(range.Range, "HorizontalAlignment"),
                VerticalAlignment = TryRead(range.Range, "VerticalAlignment"),
            };

            if (IsMissing(snapshot.HorizontalAlignment))
            {
                if (!allowCellwiseAlignment)
                {
                    return null;
                }

                snapshot.HorizontalAlignments = ReadMatrix(
                    range.Range,
                    "HorizontalAlignment",
                    range.Rows,
                    range.Columns);
                if (HasMissing(snapshot.HorizontalAlignments))
                {
                    return null;
                }
            }

            if (IsMissing(snapshot.VerticalAlignment))
            {
                if (!allowCellwiseAlignment)
                {
                    return null;
                }

                snapshot.VerticalAlignments = ReadMatrix(
                    range.Range,
                    "VerticalAlignment",
                    range.Rows,
                    range.Columns);
                if (HasMissing(snapshot.VerticalAlignments))
                {
                    return null;
                }
            }

            return snapshot;
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
                TryWrite(font, "Name", format.FontName);
                TryWrite(font, "Underline", format.FontUnderline);
                TryWrite(font, "Strikethrough", format.FontStrikethrough);
                // 字色分两条路：跟随主题的写回 ThemeColor，显式颜色的写回 Color。
                //
                // 实测（同一个格子，依次操作）：
                //   初始跟随主题        Color=0    ColorIndex=1  ThemeColor=2
                //   被改成红            Color=255  ColorIndex=3  ThemeColor=null
                //   只写回 Color        Color=0    ColorIndex=1  ThemeColor=null  ← 颜色对了，联动丢了
                //   写回 ThemeColor     Color=0    ColorIndex=1  ThemeColor=2     ← 完整还原
                //
                // 「跟随主题」和「显式黑色」的 Color 与 ColorIndex 逐字相同
                // （都是 0 和 1），只有 ThemeColor 分得开。只写 Color 的话，
                // 撤销之后文字不再跟随主题——换主题或深色模式下才看得出来，
                // 那时已经无从追溯是哪一次撤销做的。
                //
                // 也不能因此跳过不写：操作可能已经把字色改成了别的颜色，
                // 跳过就等于撤销不生效。
                if (IsMissing(format.FontThemeColor))
                {
                    TryWrite(font, "Color", format.FontColor);
                }
                else
                {
                    // 顺序要紧：先 ThemeColor 建立联动，再 TintAndShade 定深浅。
                    TryWrite(font, "ThemeColor", format.FontThemeColor);
                    TryWrite(font, "TintAndShade", format.FontTintAndShade);
                }

                // 填充的图案与颜色各有各的可信判据，不能共用一个。
                //
                // Interior.Color 在「颜色不统一」时返回 0，而那与「整片真的是黑色」
                // 的读数逐字相同——实测两种情形都是 Pattern=1、Color=0。所以既不能
                // 只看 Color 是不是缺失（0 不是缺失值），也不能只看 Pattern
                // （颜色不同而图案都为实心时，Pattern 是统一的 1，守卫会放行）。
                // 写回那个 0 就是把一片彩色刷成黑的：撤销本该还原，却成了二次破坏。
                //
                // ColorIndex 是那个诚实的属性：颜色不统一给 DBNull，真黑给 1。
                //
                // 但它挡不住另一种形态：**均匀无填充**。实测那时三项全是真实值
                // （Pattern=-4142、ColorIndex=-4142、Color=16777215），任何
                // 「缺失才跳过」的判据都会放行，而写回 Color 会把 Pattern 从
                // xlNone 顶成实心——实测写完 Pattern 就变成 1。于是撤销把一张
                // 普通无填充的表刷成实心白底、网格线消失。这是最常见的范围形态。
                //
                // 所以颜色要过两道：既不能是「不统一」，也不能是「本来就没有填充」。
                // 无填充时唯一该写的是 Pattern 本身。
                //
                // 不改成「先颜色后图案」：那样正确性就依赖 Pattern 那次写入成功，
                // 而 TryWrite 失败只记一行日志（见下方），失败后留下的正是同一种
                // 实心底，而且无声。宁可根本不制造那次写入。
                TryWrite(interior, "Pattern", format.InteriorPattern);
                if (!IsMissing(format.InteriorColorIndex) && !IsNoFill(format.InteriorColorIndex))
                {
                    TryWrite(interior, "Color", format.InteriorColor);
                }
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

        private static void RestoreAlignment(ResolvedRange range, AlignmentSnapshot alignment)
        {
            if (alignment.HorizontalAlignments != null)
            {
                WriteAlignmentMatrix(range, "HorizontalAlignment", alignment.HorizontalAlignments);
            }
            else
            {
                TryWrite(range.Range, "HorizontalAlignment", alignment.HorizontalAlignment);
            }

            if (alignment.VerticalAlignments != null)
            {
                WriteAlignmentMatrix(range, "VerticalAlignment", alignment.VerticalAlignments);
            }
            else
            {
                TryWrite(range.Range, "VerticalAlignment", alignment.VerticalAlignment);
            }
        }

        private static void WriteAlignmentMatrix(ResolvedRange range, string property, object[,] matrix)
        {
            object cells = null;
            try
            {
                cells = Com.Get(range.Range, "Cells");
                for (var r = 0; r < range.Rows; r++)
                {
                    for (var c = 0; c < range.Columns; c++)
                    {
                        var value = matrix[r, c];
                        if (IsMissing(value))
                        {
                            throw new ToolException("SNAPSHOT_INCOMPLETE", "对齐快照不完整，无法安全还原。");
                        }

                        object cell = null;
                        try
                        {
                            cell = Com.Get(cells, "Item", r + 1, c + 1);
                            // Excel 返回的对齐值是 COM 变体；逐格写回时明确转成枚举整数，
                            // 避免 IDispatch 把变体当成不合法的 Range 属性值。
                            Com.Set(cell, property, Convert.ToInt32(value, CultureInfo.InvariantCulture));
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

        private static object TryRead(object target, string name)
        {
            return Com.TryGet(target, name, out var value) ? value : null;
        }

        private static bool IsMissing(object value)
        {
            return value == null || value is DBNull;
        }

        /// <summary>xlNone。填充相关的读数取到这个值时，表示这片范围本来没有填充。</summary>
        private const int NoFill = -4142;

        /// <summary>
        /// 这个读数是不是「本来就没有填充」。
        ///
        /// 与「读不出统一值」是两件事，两者都不能写回颜色，但原因不同：
        /// 前者写回会凭空造出一层填充，后者写回会抹掉参差。
        /// </summary>
        private static bool IsNoFill(object value)
        {
            if (IsMissing(value))
            {
                return false;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture) == NoFill;
            }
            catch
            {
                return false;
            }
        }

        private static void TryWrite(object target, string name, object value)
        {
            // null 表示原范围内该属性并不统一，跳过比猜一个值更安全。
            if (IsMissing(value))
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
        /// 只采集适配会修改的水平、垂直对齐，不读数字格式矩阵。
        ///
        /// 范围级对齐统一时快照成本为 O(行+列)；对齐混合时需逐格保存，
        /// 只有在单元格数量可控且全部采集成功时才登记撤销。
        /// </summary>
        Alignment = 8,

        /// <summary>
        /// 范围内已有的合并区域。
        ///
        /// 采集成本为 O(单元格)：宿主没有「列出这片里的合并区域」的成员，
        /// 只能逐格读 MergeArea。范围级 MergeCells 为 false 时可一次断定没有合并。
        /// </summary>
        Merge = 16,

        All = Content | Format | Size,
    }
}
