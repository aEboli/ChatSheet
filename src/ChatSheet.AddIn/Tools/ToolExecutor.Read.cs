using System;
using System.Collections.Generic;
using System.Globalization;
using ChatSheet.AddIn.Hosts;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Tools
{
    /// <summary>
    /// 工具执行器。把模型的工具调用翻译成宿主 COM 操作。
    /// 全程后期绑定，不引用 Office PIA。
    /// </summary>
    internal sealed partial class ToolExecutor
    {
        private readonly Func<object> _applicationAccessor;
        private readonly RangeResolver _resolver;
        private readonly WorkbookContext _context;

        private readonly UndoStore _undo;

        internal ToolExecutor(Func<object> applicationAccessor)
        {
            _applicationAccessor = applicationAccessor ?? throw new ArgumentNullException(nameof(applicationAccessor));
            _resolver = new RangeResolver(applicationAccessor);
            _context = new WorkbookContext(applicationAccessor);
            _undo = new UndoStore(applicationAccessor, _resolver);
        }

        internal UndoStore Undo => _undo;

        private object Application =>
            _applicationAccessor() ?? throw new InvalidOperationException("尚未连接到宿主应用程序。");

        /// <summary>
        /// 执行一个工具调用。
        /// 可预期的错误一律转成结构化结果回传模型，不向上抛出。
        /// </summary>
        internal ToolResult Execute(string name, JObject args)
        {
            return Execute(name, args, undoId: null);
        }

        /// <summary>
        /// 执行工具，并在提供 undoId 时登记撤销记录。
        ///
        /// 快照在工具执行前后各采集一次：撤销还原「前」，恢复还原「后」。
        /// 两个方向对称，不必重跑工具——重跑对写入类操作不安全，
        /// 因为期间用户可能已手工改过同一片区域。
        /// </summary>
        internal ToolResult Execute(string name, JObject args, string undoId)
        {
            var definition = ToolCatalog.Find(name);
            var tracking = undoId != null
                && definition != null
                && definition.Risk != ToolRisk.Read;

            RangeSnapshot before = null;
            var detail = SnapshotDetail.None;

            if (tracking)
            {
                detail = UndoStore.DetailFor(name);
                if (detail != SnapshotDetail.None)
                {
                    before = TryCapture(args, detail);
                    // 采集失败就不登记撤销，而不是让整个操作失败——
                    // 用户宁可少一个撤销按钮，也不愿操作被拒。
                    if (before == null)
                    {
                        tracking = false;
                    }
                }
            }

            var result = ExecuteCore(name, args);

            if (tracking && result.Ok)
            {
                TryRegisterUndo(undoId, name, args, before, detail, result);
            }

            return result;
        }

        private RangeSnapshot TryCapture(JObject args, SnapshotDetail detail)
        {
            try
            {
                var address = args?.Value<string>("range");
                if (string.IsNullOrWhiteSpace(address))
                {
                    return null;
                }

                using (var range = _resolver.Resolve(address, args.Value<string>("sheet")))
                {
                    // 超大范围的快照会占用可观内存，且这类操作本就被上限拦住。
                    if (range.CellCount > ToolLimits.MaxWriteCells)
                    {
                        return null;
                    }

                    return SnapshotCapture.Capture(range, detail);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("采集撤销快照失败：" + ex.Message);
                return null;
            }
        }

        private void TryRegisterUndo(
            string undoId,
            string name,
            JObject args,
            RangeSnapshot before,
            SnapshotDetail detail,
            ToolResult result)
        {
            try
            {
                var record = new UndoRecord
                {
                    Id = undoId,
                    ToolName = name,
                    At = DateTime.Now,
                    Summary = DescribeForUndo(name, args, result),
                    ArgumentsJson = args?.ToString(Newtonsoft.Json.Formatting.None),
                };

                var structureKind = UndoStore.StructureKindFor(name);
                if (structureKind != StructureKind.None)
                {
                    record.Structure = BuildStructureAction(structureKind, args, result);
                }
                else
                {
                    record.Before = before;
                    // 操作后再采一次作为恢复依据。
                    record.After = TryCapture(args, detail);
                }

                _undo.Add(record);
            }
            catch (Exception ex)
            {
                // 撤销登记失败不应影响已成功的操作。
                Log.Warn("登记撤销记录失败：" + ex.Message);
            }
        }

        private static StructureAction BuildStructureAction(StructureKind kind, JObject args, ToolResult result)
        {
            var data = result.Data == null ? null : JObject.FromObject(result.Data);

            switch (kind)
            {
                case StructureKind.AddedWorksheet:
                    return new StructureAction
                    {
                        Kind = kind,
                        Name = data?.Value<string>("created_sheet") ?? args.Value<string>("name"),
                        AfterSheet = args.Value<string>("after_sheet"),
                    };

                case StructureKind.RenamedWorksheet:
                    return new StructureAction
                    {
                        Kind = kind,
                        Name = data?.Value<string>("new_name") ?? args.Value<string>("new_name"),
                        PreviousName = args.Value<string>("old_name"),
                    };

                case StructureKind.CreatedTable:
                    return new StructureAction
                    {
                        Kind = kind,
                        Name = data?.Value<string>("table_name"),
                        SheetName = data?.Value<string>("sheet") ?? args.Value<string>("sheet"),
                    };

                case StructureKind.CreatedChart:
                    return new StructureAction
                    {
                        Kind = kind,
                        // 图表名在创建结果里没有回传，撤销时按范围定位不可靠，
                        // 因此这里留空并在撤销时给出明确提示。
                        Name = data?.Value<string>("chart_name"),
                        SheetName = data?.Value<string>("sheet") ?? args.Value<string>("sheet"),
                    };

                default:
                    return null;
            }
        }

        /// <summary>撤销条目的一句话描述。要让用户看得懂自己在撤销什么。</summary>
        private static string DescribeForUndo(string name, JObject args, ToolResult result)
        {
            var data = result.Data == null ? null : JObject.FromObject(result.Data);
            var sheet = data?.Value<string>("sheet") ?? args?.Value<string>("sheet");
            var address = data?.Value<string>("address") ?? args?.Value<string>("range");
            var where = string.IsNullOrEmpty(sheet) ? address : $"{sheet}!{address}";

            switch (name)
            {
                case "write_values":
                    return $"写入值 {where}";
                case "write_formulas":
                    return $"写入公式 {where}";
                case "format_range":
                    return $"设置格式 {where}";
                case "set_number_format":
                    return $"设置数字格式 {where}";
                case "autofit_range":
                    return $"自动调整 {where}";
                case "clear_range":
                    return $"清除 {where}";
                case "sort_range":
                    return $"排序 {where}";
                case "add_worksheet":
                    return $"新增工作表 {data?.Value<string>("created_sheet") ?? args?.Value<string>("name")}";
                case "rename_worksheet":
                    return $"重命名工作表 {args?.Value<string>("old_name")} → {data?.Value<string>("new_name")}";
                case "create_table":
                    return $"创建表格 {data?.Value<string>("table_name")}";
                case "create_chart":
                    return $"创建图表（{where}）";
                default:
                    return name;
            }
        }

        private ToolResult ExecuteCore(string name, JObject args)
        {
            try
            {
                switch (name)
                {
                    case "get_workbook_info":
                        return GetWorkbookInfo();
                    case "get_selection":
                        return GetSelection();
                    case "read_range":
                        return ReadRange(args);
                    case "write_values":
                        return WriteMatrix(args, formulas: false);
                    case "write_formulas":
                        return WriteMatrix(args, formulas: true);
                    case "format_range":
                        return FormatRange(args);
                    case "set_number_format":
                        return SetNumberFormat(args);
                    case "autofit_range":
                        return AutofitRange(args);
                    case "clear_range":
                        return ClearRange(args);
                    case "add_worksheet":
                        return AddWorksheet(args);
                    case "rename_worksheet":
                        return RenameWorksheet(args);
                    case "sort_range":
                        return SortRange(args);
                    case "create_table":
                        return CreateTable(args);
                    case "create_chart":
                        return CreateChart(args);
                    default:
                        return ToolResult.Failure("UNKNOWN_TOOL", $"未知工具：{name}。");
                }
            }
            catch (ToolException ex)
            {
                Log.Warn($"工具 {name} 返回可预期错误：{ex.Code} {ex.Message}");
                return ToolResult.Failure(ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                // 必须剥开 TargetInvocationException：后期绑定调用失败时，
                // 外层异常只会说「调用的目标发生了异常」，真正原因在内层。
                // 不剥开的话错误信息对模型和排查都毫无价值。
                var root = Unwrap(ex);
                if (root is ToolException toolError)
                {
                    Log.Warn($"工具 {name} 返回可预期错误：{toolError.Code} {toolError.Message}");
                    return ToolResult.Failure(toolError.Code, toolError.Message);
                }

                Log.Error($"工具 {name} 执行失败", root);
                return ToolResult.Failure("HOST_ERROR", $"宿主操作失败：{root.Message}");
            }
        }

        /// <summary>剥离反射调用的包装异常，取出真正的失败原因。</summary>
        private static Exception Unwrap(Exception ex)
        {
            var current = ex;
            var depth = 0;
            while (current is System.Reflection.TargetInvocationException && current.InnerException != null && depth < 5)
            {
                current = current.InnerException;
                depth++;
            }

            return current;
        }

        private ToolResult GetWorkbookInfo()
        {
            var summary = _context.GetSummary();
            if (!summary.HasWorkbook)
            {
                return ToolResult.Failure("NO_WORKBOOK", "当前没有打开的工作簿。");
            }

            var sheets = new List<object>();
            foreach (var sheet in summary.Sheets)
            {
                sheets.Add(new Dictionary<string, object>
                {
                    ["name"] = sheet.Name,
                    ["used_range"] = sheet.UsedRange,
                    ["rows"] = sheet.RowCount,
                    ["columns"] = sheet.ColumnCount,
                });
            }

            return ToolResult.Success(new Dictionary<string, object>
            {
                ["workbook"] = summary.Name,
                ["saved"] = summary.Saved,
                ["active_sheet"] = summary.ActiveSheet,
                ["sheet_count"] = summary.SheetCount,
                ["sheets"] = sheets,
            });
        }

        private ToolResult GetSelection()
        {
            var selection = _context.GetSelection();
            if (!selection.HasSelection)
            {
                return ToolResult.Failure("NO_SELECTION", "当前没有选区。");
            }

            return ToolResult.Success(new Dictionary<string, object>
            {
                ["sheet"] = selection.SheetName,
                ["address"] = selection.Address,
                ["rows"] = selection.RowCount,
                ["columns"] = selection.ColumnCount,
            });
        }

        private ToolResult ReadRange(JObject args)
        {
            var address = RequireString(args, "range");
            var sheet = OptionalString(args, "sheet");
            var includeFormulas = OptionalBool(args, "include_formulas") ?? false;

            using (var range = _resolver.Resolve(address, sheet))
            {
                RangeResolver.AssertCellLimit(range, ToolLimits.MaxReadCells, "读取");

                var values = ReadMatrix(range, "Value2");
                var payload = new Dictionary<string, object>
                {
                    ["sheet"] = range.SheetName,
                    ["address"] = range.Address,
                    ["rows"] = range.Rows,
                    ["columns"] = range.Columns,
                    ["values"] = values,
                };

                if (includeFormulas)
                {
                    payload["formulas"] = ReadMatrix(range, "Formula");
                }

                return ToolResult.Success(payload);
            }
        }

        /// <summary>
        /// 读取矩阵。单个单元格时宿主返回标量而非二维数组，
        /// 这里统一成二维结构，避免模型面对两种形态。
        /// </summary>
        private static List<List<object>> ReadMatrix(ResolvedRange range, string property)
        {
            var raw = Com.Get(range.Range, property);
            var result = new List<List<object>>();

            if (raw is Array array && array.Rank == 2)
            {
                var lowerRow = array.GetLowerBound(0);
                var upperRow = array.GetUpperBound(0);
                var lowerCol = array.GetLowerBound(1);
                var upperCol = array.GetUpperBound(1);

                for (var r = lowerRow; r <= upperRow; r++)
                {
                    var row = new List<object>();
                    for (var c = lowerCol; c <= upperCol; c++)
                    {
                        row.Add(Normalize(array.GetValue(r, c)));
                    }

                    result.Add(row);
                }

                return result;
            }

            result.Add(new List<object> { Normalize(raw) });
            return result;
        }

        /// <summary>把 COM 值规范成 JSON 友好形态，并截断超长文本。</summary>
        private static object Normalize(object value)
        {
            if (value == null || value is DBNull)
            {
                return null;
            }

            if (value is string text)
            {
                return text.Length > ToolLimits.MaxCellTextLength
                    ? text.Substring(0, ToolLimits.MaxCellTextLength) + "…（已截断）"
                    : text;
            }

            if (value is DateTime dt)
            {
                return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (value is bool || value is double || value is int || value is long || value is decimal || value is float)
            {
                return value;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
