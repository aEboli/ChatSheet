using System;
using System.Globalization;
using ChatSheet.AddIn.Hosts;

namespace ChatSheet.AddIn.Tools
{
    /// <summary>已解析的范围，附带尺寸信息用于上限校验。</summary>
    internal sealed class ResolvedRange : IDisposable
    {
        internal ResolvedRange(object worksheet, object range, string sheetName, string address, int rows, int columns)
        {
            Worksheet = worksheet;
            Range = range;
            SheetName = sheetName;
            Address = address;
            Rows = rows;
            Columns = columns;
        }

        internal object Worksheet { get; }

        internal object Range { get; }

        internal string SheetName { get; }

        internal string Address { get; }

        internal int Rows { get; }

        internal int Columns { get; }

        internal int CellCount => Rows * Columns;

        public void Dispose()
        {
            Com.Release(Range);
            Com.Release(Worksheet);
        }
    }

    /// <summary>
    /// 范围解析。集中处理工作表定位、地址解析与尺寸计算，
    /// 使各工具实现不必重复这些易错的 COM 细节。
    /// </summary>
    internal sealed class RangeResolver
    {
        private readonly Func<object> _applicationAccessor;

        internal RangeResolver(Func<object> applicationAccessor)
        {
            _applicationAccessor = applicationAccessor;
        }

        private object Application =>
            _applicationAccessor() ?? throw new InvalidOperationException("尚未连接到宿主应用程序。");

        /// <summary>按名称取工作表；名称为空时取活动工作表。</summary>
        internal object ResolveWorksheet(string sheetName)
        {
            if (string.IsNullOrWhiteSpace(sheetName))
            {
                var active = Com.Get(Application, "ActiveSheet");
                if (active == null)
                {
                    throw new ToolException("NO_ACTIVE_SHEET", "当前没有活动工作表。");
                }

                return active;
            }

            object workbook = null;
            object sheets = null;
            try
            {
                workbook = Com.Get(Application, "ActiveWorkbook")
                    ?? throw new ToolException("NO_WORKBOOK", "当前没有打开的工作簿。");
                sheets = Com.Get(workbook, "Worksheets");

                try
                {
                    return Com.Get(sheets, "Item", sheetName);
                }
                catch (Exception)
                {
                    // 把 COM 的模糊错误换成模型能理解的提示，并列出可选名称。
                    var available = ListSheetNames(sheets);
                    throw new ToolException(
                        "SHEET_NOT_FOUND",
                        $"找不到工作表「{sheetName}」。现有工作表：{available}。");
                }
            }
            finally
            {
                Com.Release(sheets);
                Com.Release(workbook);
            }
        }

        private static string ListSheetNames(object sheets)
        {
            try
            {
                var count = Convert.ToInt32(Com.Get(sheets, "Count"), CultureInfo.InvariantCulture);
                var names = new string[Math.Min(count, 30)];
                for (var i = 0; i < names.Length; i++)
                {
                    object sheet = null;
                    try
                    {
                        sheet = Com.Get(sheets, "Item", i + 1);
                        names[i] = Com.GetString(sheet, "Name");
                    }
                    finally
                    {
                        Com.Release(sheet);
                    }
                }

                return string.Join("、", names);
            }
            catch
            {
                return "<无法枚举>";
            }
        }

        /// <summary>解析范围地址，并返回尺寸信息。</summary>
        internal ResolvedRange Resolve(string address, string sheetName)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ToolException("RANGE_REQUIRED", "必须提供范围地址。");
            }

            object worksheet = null;
            object range = null;
            try
            {
                worksheet = ResolveWorksheet(sheetName);
                var resolvedSheetName = Com.GetString(worksheet, "Name");

                try
                {
                    range = Com.Get(worksheet, "Range", address);
                }
                catch (Exception)
                {
                    throw new ToolException(
                        "RANGE_INVALID",
                        $"范围地址「{address}」无法解析。请使用 A1、A1:D20 或 A:D 这类形式。");
                }

                if (range == null)
                {
                    throw new ToolException("RANGE_INVALID", $"范围地址「{address}」无效。");
                }

                var rows = CountOf(range, "Rows");
                var columns = CountOf(range, "Columns");
                var actualAddress = Com.GetString(range, "Address");

                var resolved = new ResolvedRange(worksheet, range, resolvedSheetName, actualAddress, rows, columns);
                worksheet = null;
                range = null;
                return resolved;
            }
            finally
            {
                Com.Release(range);
                Com.Release(worksheet);
            }
        }

        private static int CountOf(object range, string member)
        {
            object collection = null;
            try
            {
                collection = Com.Get(range, member);
                return Convert.ToInt32(Com.Get(collection, "Count"), CultureInfo.InvariantCulture);
            }
            finally
            {
                Com.Release(collection);
            }
        }

        /// <summary>校验单元格数量上限，超限时抛出模型可读的错误。</summary>
        internal static void AssertCellLimit(ResolvedRange range, int limit, string operation)
        {
            if (range.CellCount <= limit)
            {
                return;
            }

            throw new ToolException(
                "RANGE_TOO_LARGE",
                $"{operation}涉及 {range.CellCount} 个单元格，超过上限 {limit}。" +
                $"范围 {range.Address} 为 {range.Rows} 行 × {range.Columns} 列，请改用更小的范围分批处理。");
        }
    }

    /// <summary>工具层的可预期错误。这类错误会作为结构化结果回传给模型，而不是崩溃。</summary>
    internal sealed class ToolException : Exception
    {
        internal ToolException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        internal string Code { get; }
    }
}
