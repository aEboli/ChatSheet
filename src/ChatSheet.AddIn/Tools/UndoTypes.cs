using System;
using System.Collections.Generic;

namespace ChatSheet.AddIn.Tools
{
    /// <summary>
    /// 范围快照。记录足以还原一片单元格的全部信息。
    ///
    /// 存 Formula 而非 Value2：对含公式的单元格它保留公式本身，
    /// 对普通单元格它就是字面值，一份数据即可还原两种情形。
    /// </summary>
    internal sealed class RangeSnapshot
    {
        internal string SheetName { get; set; }

        internal string Address { get; set; }

        internal int Rows { get; set; }

        internal int Columns { get; set; }

        /// <summary>公式或字面值矩阵。</summary>
        internal object[,] Formulas { get; set; }

        /// <summary>数字格式矩阵。清除格式类操作需要它才能还原。</summary>
        internal object[,] NumberFormats { get; set; }

        /// <summary>字体与填充等外观属性。仅格式类操作会采集。</summary>
        internal FormatSnapshot Format { get; set; }

        /// <summary>
        /// 适配操作修改的对齐快照。
        /// 统一对齐保留范围级值；混合对齐则保留逐格值，避免撤销时把原有排版抹平。
        /// </summary>
        internal AlignmentSnapshot Alignment { get; set; }

        /// <summary>列宽与行高。仅自动调整类操作会采集。</summary>
        internal double[] ColumnWidths { get; set; }

        internal double[] RowHeights { get; set; }

        /// <summary>
        /// 范围内已有的合并区域地址。仅合并类操作会采集。
        ///
        /// 空列表与 null 含义不同：空列表表示「采过，当时没有合并」，
        /// 还原时要把范围拆平；null 表示这个维度没采，还原时不该碰合并状态。
        /// </summary>
        internal IReadOnlyList<string> MergeAreas { get; set; }

        /// <summary>
        /// 格式维度采到了，但整片范围的外观属性逐项都不一致，还原时全部跳过。
        ///
        /// 只在还有别的维度值得还原时才会出现（清除同时留了内容底），
        /// 用来把「内容能撤、格式撤不回」如实说给用户。格式是唯一维度时
        /// 不设这个标记——那种情况根本不登记记录。
        /// </summary>
        internal bool FormatIncomplete { get; set; }

        /// <summary>
        /// 这次操作会抹掉快照根本不覆盖的格式维度。
        ///
        /// 边框不在采集范围内：一片范围有四条外边、两条内线加两条对角线，
        /// 每条各有线型、粗细、颜色，逐边读的 COM 成本远高于其余全部九项之和，
        /// 而范围级读取在不一致时又只给 null。因此边框不采。
        ///
        /// 平时无妨——只改字体或填充的操作不碰边框。但 ClearFormats 与 Clear
        /// 会把边框一并抹掉，此时撤销还原了九项、边框永久消失，而卡片上
        /// 什么也没说。设这个标记就是为了把这句话说出来：
        /// 撤销做不到的部分必须写在卡上，这是本项目一贯的取舍。
        /// </summary>
        internal bool ClearsUncapturedFormats { get; set; }
    }

    /// <summary>
    /// 外观属性快照。
    ///
    /// 只在整片范围属性一致时才有意义；范围内属性不一致时宿主返回 null，
    /// 此处如实记录 null，还原时跳过该属性——强行写入一个猜测值
    /// 会把原本参差的格式抹平，那比不还原更糟。
    /// </summary>
    internal sealed class FormatSnapshot
    {
        internal object Bold { get; set; }

        internal object Italic { get; set; }

        internal object FontSize { get; set; }

        /// <summary>字体名。清除格式会把它重置成主题正文字体。</summary>
        internal object FontName { get; set; }

        /// <summary>下划线。</summary>
        internal object FontUnderline { get; set; }

        /// <summary>删除线。</summary>
        internal object FontStrikethrough { get; set; }

        internal object FontColor { get; set; }

        /// <summary>
        /// 字体颜色的主题联动。只用来判断 <see cref="FontColor"/> 该不该写回，不参与还原。
        ///
        /// 未设过字色的单元格跟随主题：实测 Color=0、ColorIndex=1、**ThemeColor=2**。
        /// 显式设成黑色的读数是 Color=0、ColorIndex=1、ThemeColor 为空——前两项
        /// 逐字相同，只有这一项不同。把采到的 Color=0 原样写回，ThemeColor 会从 2
        /// 变成 null，于是撤销之后那段文字不再跟随主题。换主题或在深色模式下
        /// 才看得出来，而那时已经无从追溯是哪一次撤销做的。
        ///
        /// 与填充那边同构（Interior.Color 的 0 也是个骗人的读数），但判据不同：
        /// 填充看 ColorIndex，字体看 ThemeColor。
        /// </summary>
        internal object FontThemeColor { get; set; }

        /// <summary>
        /// 主题色的深浅。与 <see cref="FontThemeColor"/> 成对还原。
        ///
        /// 主题色允许「在主题色基础上调亮/调暗」（用户界面上那一栏的浅色变体），
        /// 只写回 ThemeColor 会把深浅重置成 0，字色跳回主题的原色。
        /// </summary>
        internal object FontTintAndShade { get; set; }

        internal object InteriorColor { get; set; }

        /// <summary>
        /// 填充颜色的索引。只用来判断 <see cref="InteriorColor"/> 可不可信，不参与还原。
        ///
        /// 必须有这一项：范围内填充色不同时，宿主对 Interior.Color 返回 0，
        /// 而那与「整片真的是黑色」的读数**逐字相同**（实测两种情形都是
        /// Pattern=1、Color=0）。只看 Color 或 Pattern 分不开，写回去就把
        /// 一片彩色刷成黑的。ColorIndex 在这两种情形下不同：
        /// 颜色不统一给 DBNull，真黑给 1。
        /// </summary>
        internal object InteriorColorIndex { get; set; }

        internal object InteriorPattern { get; set; }

        internal object HorizontalAlignment { get; set; }

        internal object VerticalAlignment { get; set; }

        internal object WrapText { get; set; }

        /// <summary>
        /// 九个属性是否全为 null，即每一项在范围内都不一致。
        ///
        /// 还原时 null 一律跳过（写入猜测值会把参差的格式抹平，那比不还原更糟），
        /// 所以全 null 的快照什么也还原不了。仅凭它登记撤销，会做出一个
        /// 点下去标成「已撤销」而格子毫无变化的按钮。
        /// </summary>
        internal bool IsAllNull => MissingCount == 9;

        /// <summary>
        /// 是否有任一属性在范围内不一致。
        ///
        /// 与 <see cref="IsAllNull"/> 分开是必须的：真实的「格式不统一」几乎总是
        /// 部分不一致——加粗和字号各格不同，而斜体、换行、对齐仍然统一。
        /// 这种快照还原得回一部分，所以记录要留，但必须标明只能部分还原；
        /// 若按「全 null 才算混合」判断，用户会拿到一个自称完整的撤销。
        /// </summary>
        internal bool HasMixedProperty => MissingCount > 0;

        /// <summary>
        /// 有几项读不出统一值。
        ///
        /// 判据必须与还原时跳过属性的那个判据逐字相同：宿主对混合范围返回的是
        /// <see cref="DBNull"/> 而不是 CLR null，只比 == null 会得出「没有一项混合」
        /// 的相反结论，于是把一条只能部分还原的记录当成完整的。
        /// </summary>
        private int MissingCount
        {
            get
            {
                var count = 0;
                if (IsMissing(Bold)) { count++; }
                if (IsMissing(Italic)) { count++; }
                if (IsMissing(FontSize)) { count++; }
                if (IsMissing(FontColor)) { count++; }
                if (IsMissing(InteriorPattern)) { count++; }
                if (IsMissing(HorizontalAlignment)) { count++; }
                if (IsMissing(VerticalAlignment)) { count++; }
                if (IsMissing(WrapText)) { count++; }

                // 填充颜色按 ColorIndex 判断，不按 Color 自己。
                //
                // Color 在「颜色不统一」时返回 0，看着像有值，而那与真黑色的读数
                // 完全相同。计数必须与 RestoreFormat 实际写不写它逐字一致，
                // 否则「能不能完整还原」这个问题会在卡片和撤销里得到两个答案。
                if (IsMissing(InteriorColorIndex)) { count++; }

                return count;
            }
        }

        private static bool IsMissing(object value)
        {
            return value == null || value is DBNull;
        }
    }

    /// <summary>
    /// 适配操作的水平与垂直对齐快照。
    ///
    /// Excel 对混合范围的范围级对齐属性返回 null。此时只能逐格保存；若范围过大
    /// 无法安全保留逐格数据，就不创建撤销记录，而不是假装能够完整还原。
    /// </summary>
    internal sealed class AlignmentSnapshot
    {
        internal object HorizontalAlignment { get; set; }

        internal object VerticalAlignment { get; set; }

        internal object[,] HorizontalAlignments { get; set; }

        internal object[,] VerticalAlignments { get; set; }
    }

    /// <summary>结构类操作的逆向信息。这类操作无法用范围快照表达。</summary>
    internal enum StructureKind
    {
        None = 0,
        AddedWorksheet = 1,
        RenamedWorksheet = 2,
        CreatedTable = 3,
        CreatedChart = 4,
    }

    internal sealed class StructureAction
    {
        internal StructureKind Kind { get; set; }

        /// <summary>新增工作表的名称，或重命名后的名称。</summary>
        internal string Name { get; set; }

        /// <summary>重命名前的名称。</summary>
        internal string PreviousName { get; set; }

        /// <summary>表格或图表所在的工作表。</summary>
        internal string SheetName { get; set; }

        /// <summary>新增工作表时它前一张表的名称，用于恢复时放回原位。</summary>
        internal string AfterSheet { get; set; }

        /// <summary>
        /// 撤销之后还能不能恢复。
        ///
        /// 图表是唯一撤销可行、恢复不可行的结构操作：删掉之后无法自动重建。
        /// 没有这个标记时 <see cref="UndoRecord.CanRedo"/> 会对任何结构记录为真，
        /// 面板于是把按钮改成「恢复」，再点一次必然拿到 UNSUPPORTED——
        /// 谎言只是从「撤销」挪到了「恢复」。
        /// </summary>
        internal bool CanRestore { get; set; } = true;
    }

    /// <summary>一条可撤销的操作记录。</summary>
    internal sealed class UndoRecord
    {
        internal string Id { get; set; }

        internal string ToolName { get; set; }

        /// <summary>展示给用户的一句话描述。</summary>
        internal string Summary { get; set; }

        internal DateTime At { get; set; }

        /// <summary>当前是否处于已撤销状态。</summary>
        internal bool Undone { get; set; }

        /// <summary>操作前的状态，撤销时还原。</summary>
        internal RangeSnapshot Before { get; set; }

        /// <summary>操作后的状态，恢复时还原。</summary>
        internal RangeSnapshot After { get; set; }

        /// <summary>结构类操作的逆向信息。</summary>
        internal StructureAction Structure { get; set; }

        /// <summary>原始参数，结构类操作恢复时需要重放。</summary>
        internal string ArgumentsJson { get; set; }

        internal bool CanUndo => !Undone && (Before != null || Structure != null);

        internal bool CanRedo => Undone
            && (After != null || (Structure != null && Structure.CanRestore));
    }

    /// <summary>撤销或恢复的结果。</summary>
    internal sealed class UndoOutcome
    {
        internal bool Ok { get; set; }

        internal string Message { get; set; }

        internal string ErrorCode { get; set; }

        internal bool Undone { get; set; }

        internal static UndoOutcome Success(string message, bool undone)
        {
            return new UndoOutcome { Ok = true, Message = message, Undone = undone };
        }

        internal static UndoOutcome Failure(string code, string message)
        {
            return new UndoOutcome { Ok = false, ErrorCode = code, Message = message };
        }
    }
}
