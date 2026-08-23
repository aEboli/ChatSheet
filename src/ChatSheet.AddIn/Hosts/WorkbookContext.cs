using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChatSheet.AddIn.Hosts
{
    /// <summary>
    /// 工作簿上下文读取。全程后期绑定，Excel 与 WPS 表格共用同一份实现。
    /// 这些信息会作为上下文注入模型，因此必须控制体积：
    /// 只给结构与选区摘要，不给全量单元格。
    /// </summary>
    internal sealed class WorkbookContext
    {
        private readonly Func<object> _applicationAccessor;

        internal WorkbookContext(Func<object> applicationAccessor)
        {
            _applicationAccessor = applicationAccessor ?? throw new ArgumentNullException(nameof(applicationAccessor));
        }

        private object Application =>
            _applicationAccessor() ?? throw new InvalidOperationException("尚未连接到宿主应用程序。");

        /// <summary>当前活动工作簿的结构摘要。</summary>
        internal WorkbookSummary GetSummary()
        {
            object workbook = null;
            object sheets = null;
            try
            {
                workbook = Com.Get(Application, "ActiveWorkbook");
                if (workbook == null)
                {
                    return new WorkbookSummary { HasWorkbook = false };
                }

                sheets = Com.Get(workbook, "Worksheets");
                var count = Convert.ToInt32(Com.Get(sheets, "Count"), CultureInfo.InvariantCulture);
                var summary = new WorkbookSummary
                {
                    HasWorkbook = true,
                    Name = Com.GetString(workbook, "Name"),
                    Saved = TryBool(workbook, "Saved"),
                    SheetCount = count,
                };

                for (var i = 1; i <= count; i++)
                {
                    object sheet = null;
                    try
                    {
                        sheet = Com.Get(sheets, "Item", i);
                        summary.Sheets.Add(ReadSheet(sheet));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"读取第 {i} 张工作表失败：{ex.Message}");
                    }
                    finally
                    {
                        Com.Release(sheet);
                    }
                }

                var active = ReadActiveSheetName();
                summary.ActiveSheet = active;
                return summary;
            }
            finally
            {
                Com.Release(sheets);
                Com.Release(workbook);
            }
        }

        private SheetSummary ReadSheet(object sheet)
        {
            object used = null;
            try
            {
                var info = new SheetSummary
                {
                    Name = Com.GetString(sheet, "Name"),
                };

                // UsedRange 是判断数据规模的关键；空表时它仍返回 A1，需要结合计数判断。
                if (Com.TryGet(sheet, "UsedRange", out used) && used != null)
                {
                    info.UsedRange = Com.GetString(used, "Address");
                    if (Com.TryGet(used, "Rows", out var rows) && rows != null)
                    {
                        info.RowCount = Convert.ToInt32(Com.Get(rows, "Count"), CultureInfo.InvariantCulture);
                        Com.Release(rows);
                    }

                    if (Com.TryGet(used, "Columns", out var cols) && cols != null)
                    {
                        info.ColumnCount = Convert.ToInt32(Com.Get(cols, "Count"), CultureInfo.InvariantCulture);
                        Com.Release(cols);
                    }
                }

                return info;
            }
            finally
            {
                Com.Release(used);
            }
        }

        private string ReadActiveSheetName()
        {
            object sheet = null;
            try
            {
                return Com.TryGet(Application, "ActiveSheet", out sheet) && sheet != null
                    ? Com.GetString(sheet, "Name")
                    : string.Empty;
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        /// <summary>当前选区。用于让模型知道用户正在关注哪块数据。</summary>
        internal SelectionInfo GetSelection()
        {
            object selection = null;
            object sheet = null;
            try
            {
                if (!Com.TryGet(Application, "Selection", out selection) || selection == null)
                {
                    return new SelectionInfo { HasSelection = false };
                }

                var address = Com.GetString(selection, "Address");
                if (string.IsNullOrEmpty(address))
                {
                    return new SelectionInfo { HasSelection = false };
                }

                var info = new SelectionInfo
                {
                    HasSelection = true,
                    Address = address,
                };

                if (Com.TryGet(Application, "ActiveSheet", out sheet) && sheet != null)
                {
                    info.SheetName = Com.GetString(sheet, "Name");
                }

                if (Com.TryGet(selection, "Rows", out var rows) && rows != null)
                {
                    info.RowCount = Convert.ToInt32(Com.Get(rows, "Count"), CultureInfo.InvariantCulture);
                    Com.Release(rows);
                }

                if (Com.TryGet(selection, "Columns", out var cols) && cols != null)
                {
                    info.ColumnCount = Convert.ToInt32(Com.Get(cols, "Count"), CultureInfo.InvariantCulture);
                    Com.Release(cols);
                }

                return info;
            }
            finally
            {
                Com.Release(sheet);
                Com.Release(selection);
            }
        }

        private static bool TryBool(object target, string name)
        {
            return Com.TryGet(target, name, out var value) && value != null && Convert.ToBoolean(value);
        }
    }

    internal sealed class WorkbookSummary
    {
        internal bool HasWorkbook { get; set; }

        internal string Name { get; set; } = string.Empty;

        internal bool Saved { get; set; }

        internal int SheetCount { get; set; }

        internal string ActiveSheet { get; set; } = string.Empty;

        internal List<SheetSummary> Sheets { get; } = new List<SheetSummary>();

        /// <summary>生成注入模型的紧凑文本，避免用 JSON 浪费 token。</summary>
        internal string ToPromptText()
        {
            if (!HasWorkbook)
            {
                return "当前没有打开的工作簿。";
            }

            var builder = new StringBuilder();
            builder.Append("工作簿：").Append(Name)
                .Append("（共 ").Append(SheetCount).Append(" 张工作表");
            if (!string.IsNullOrEmpty(ActiveSheet))
            {
                builder.Append("，当前 ").Append(ActiveSheet);
            }

            builder.AppendLine("）");

            foreach (var sheet in Sheets)
            {
                builder.Append("- ").Append(sheet.Name);
                if (!string.IsNullOrEmpty(sheet.UsedRange))
                {
                    builder.Append("：已用范围 ").Append(sheet.UsedRange)
                        .Append("（").Append(sheet.RowCount).Append(" 行 × ")
                        .Append(sheet.ColumnCount).Append(" 列）");
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }
    }

    internal sealed class SheetSummary
    {
        internal string Name { get; set; } = string.Empty;

        internal string UsedRange { get; set; } = string.Empty;

        internal int RowCount { get; set; }

        internal int ColumnCount { get; set; }
    }

    internal sealed class SelectionInfo
    {
        internal bool HasSelection { get; set; }

        internal string SheetName { get; set; } = string.Empty;

        internal string Address { get; set; } = string.Empty;

        internal int RowCount { get; set; }

        internal int ColumnCount { get; set; }

        internal string ToPromptText()
        {
            return HasSelection
                ? $"当前选区：{SheetName}!{Address}（{RowCount} 行 × {ColumnCount} 列）"
                : "当前没有选区。";
        }
    }
}
