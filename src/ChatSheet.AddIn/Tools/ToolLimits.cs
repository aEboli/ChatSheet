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
        /// <summary>
        /// 单次读取的最大单元格数。超过则要求模型分批读取。
        ///
        /// 与写入同为 5000，约束来自上下文预算而非宿主性能：读取结果整片进对话历史，
        /// 中文密集表按估算约 8 token/单元格，5000 格即约 4 万 token。
        /// <see cref="Agent.Conversation.TrimToBudget"/> 永不压缩最近 6 条消息
        /// （约三轮工具往返），因此三条满额结果必须能容于预算的 70%，
        /// 否则压缩无处下手，只能带着超限的上下文发出。
        /// 按默认预算 200000 计，三条约 12 万 token，仍在 14 万的目标线内。
        /// </summary>
        internal const int MaxReadCells = 5_000;

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

        /// <summary>
        /// 只采对齐与尺寸时，快照的行数加列数上限。
        ///
        /// 这类快照每行每列各一次 COM 调用，单次约 0.1 毫秒，5 万取在
        /// 最坏约 5 秒——比失去撤销更可接受。超过则不登记撤销，
        /// 但操作本身照常执行。
        /// </summary>
        internal const int MaxSnapshotDimensions = 50_000;

        /// <summary>工作表名称长度上限，与 Excel 自身限制一致。</summary>
        internal const int MaxSheetNameLength = 31;
    }
}
