namespace ChatSheet.AddIn.Tools
{
    /// <summary>
    /// 工具层的硬性上限。
    ///
    /// 存在的意义：模型可能请求诸如 A1:XFD1048576 这样的全表范围。
    /// 读取过大范围会让宿主长时间无响应，写入过大范围一旦出错则难以恢复。
    /// 这些上限在执行前拦截，并把超限原因回给模型，让它改用更小的范围重试。
    /// </summary>
    internal static class ToolLimits
    {
        /// <summary>单次读取的最大单元格数。超过则要求模型分批读取。</summary>
        internal const int MaxReadCells = 2_000;

        /// <summary>单次写入的最大单元格数。写入是破坏性操作，上限更严格地执行。</summary>
        internal const int MaxWriteCells = 5_000;

        /// <summary>格式与数字格式类操作的最大单元格数。</summary>
        internal const int MaxFormatCells = MaxWriteCells;

        /// <summary>自动调整行高列宽时涉及的最大行列数。</summary>
        internal const int MaxAutofitDimensions = MaxWriteCells;

        /// <summary>清除操作的最大单元格数。</summary>
        internal const int MaxClearCells = MaxWriteCells;

        /// <summary>排序范围的最大单元格数。</summary>
        internal const int MaxSortCells = MaxWriteCells;

        /// <summary>
        /// 单个单元格文本回传给模型时的截断长度。
        /// 避免个别超长单元格挤占整个上下文预算。
        /// </summary>
        internal const int MaxCellTextLength = 512;

        /// <summary>工作表名称长度上限，与 Excel 自身限制一致。</summary>
        internal const int MaxSheetNameLength = 31;
    }
}
