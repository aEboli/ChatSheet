namespace ChatSheet.AddIn.Tools
{
    internal static class ToolLimits
    {
        internal const int ReadPageCells = 5_000;
        internal const int MaxSnapshotCells = 5_000;
        internal const long MaxReadCells = 17_179_869_184L;
        internal const long MaxWriteCells = MaxReadCells;
        internal const long MaxFormatCells = MaxReadCells;
        internal const int MaxAutofitDimensions = 1_048_576;
        internal const long MaxClearCells = MaxReadCells;
        internal const long MaxSortCells = MaxReadCells;
        internal const long MaxMergeCells = MaxReadCells;
        internal const int MaxCellTextLength = 32_767;
        internal const int MaxSnapshotDimensions = 50_000;
        internal const int MaxSheetNameLength = 31;
    }
}