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

        internal object FontColor { get; set; }

        internal object InteriorColor { get; set; }

        internal object InteriorPattern { get; set; }

        internal object HorizontalAlignment { get; set; }

        internal object VerticalAlignment { get; set; }

        internal object WrapText { get; set; }
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

        internal bool CanRedo => Undone && (After != null || Structure != null);
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
