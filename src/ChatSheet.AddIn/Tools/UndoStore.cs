using System;
using System.Collections.Generic;
using System.Linq;
using ChatSheet.AddIn.Hosts;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Tools
{
    /// <summary>
    /// 撤销栈。按工具调用记录操作前后的快照，支持撤销与恢复。
    ///
    /// 设计取舍：不做「撤销后清空后续记录」的严格栈语义，而允许对任意
    /// 一条记录单独撤销/恢复。原因是对话里的操作往往互不相干（改 A 列格式、
    /// 又在 F 列写公式），强制按顺序撤销会逼用户连带回退无关的改动。
    /// 代价是相互重叠的操作若乱序撤销可能得到意外结果，因此还原前会校验
    /// 范围尺寸，并把风险如实告知。
    /// </summary>
    internal sealed class UndoStore
    {
        /// <summary>
        /// 保留的记录条数上限。
        /// 快照按单元格存储，过多会占用可观内存；一轮对话很少超过这个量级。
        /// </summary>
        private const int MaxRecords = 60;

        private readonly List<UndoRecord> _records = new List<UndoRecord>();
        private readonly RangeResolver _resolver;
        private readonly Func<object> _applicationAccessor;

        internal UndoStore(Func<object> applicationAccessor, RangeResolver resolver)
        {
            _applicationAccessor = applicationAccessor;
            _resolver = resolver;
        }

        private object Application =>
            _applicationAccessor() ?? throw new InvalidOperationException("尚未连接到宿主应用程序。");

        internal IReadOnlyList<UndoRecord> Records => _records;

        internal UndoRecord Find(string id)
        {
            return _records.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        }

        internal void Clear()
        {
            _records.Clear();
        }

        /// <summary>登记一条记录，超出上限时丢弃最早的。</summary>
        internal void Add(UndoRecord record)
        {
            _records.Add(record);
            while (_records.Count > MaxRecords)
            {
                _records.RemoveAt(0);
            }
        }

        /// <summary>该工具是否需要采集快照，以及采集哪些维度。</summary>
        internal static SnapshotDetail DetailFor(string toolName)
        {
            switch (toolName)
            {
                case "write_values":
                case "write_formulas":
                case "sort_range":
                    return SnapshotDetail.Content;

                case "set_number_format":
                    return SnapshotDetail.Content;

                case "format_range":
                    return SnapshotDetail.Format;

                case "clear_range":
                    // 清除可能同时抹掉内容与格式，两者都要留底。
                    return SnapshotDetail.Content | SnapshotDetail.Format;

                case "autofit_range":
                    return SnapshotDetail.Size;

                case "merge_cells":
                    // 合并是唯一会静默丢值的写操作：非锚点单元格的内容被宿主直接
                    // 丢弃，所以内容必须逐格留底，否则撤销只能把格子拆回来、值找不回。
                    // 再加 Format 是因为工具可同时改对齐，Merge 则用来记下范围内
                    // 原有的合并区域——用户可能是在已有合并的版面上再合一次。
                    return SnapshotDetail.Content | SnapshotDetail.Format | SnapshotDetail.Merge;

                case "unmerge_cells":
                    // 取消合并不丢数据：原内容留在左上角，其余格本就是空的。
                    // 只需记住原有的合并区域，撤销时照原样合回去。
                    return SnapshotDetail.Merge;

                case "fit_range":
                    // 适配同时改对齐与行列尺寸，两个维度都要留底才能完整还原。
                    // 用 Alignment 而非 Format：适配不改数字格式。统一对齐保留范围级
                    // 快照；混合对齐才逐格保存，过大或不完整时不显示撤销按钮。
                    return SnapshotDetail.Alignment | SnapshotDetail.Size;

                default:
                    return SnapshotDetail.None;
            }
        }

        /// <summary>结构类操作的种类。这类操作无法用范围快照表达。</summary>
        internal static StructureKind StructureKindFor(string toolName)
        {
            switch (toolName)
            {
                case "add_worksheet": return StructureKind.AddedWorksheet;
                case "rename_worksheet": return StructureKind.RenamedWorksheet;
                case "create_table": return StructureKind.CreatedTable;
                case "create_chart": return StructureKind.CreatedChart;
                default: return StructureKind.None;
            }
        }

        /// <summary>撤销一条记录。</summary>
        internal UndoOutcome Undo(string id)
        {
            var record = Find(id);
            if (record == null)
            {
                return UndoOutcome.Failure("NOT_FOUND", "找不到该操作记录，可能已超出保留范围。");
            }

            if (record.Undone)
            {
                return UndoOutcome.Failure("ALREADY_UNDONE", "该操作已经撤销过了。");
            }

            try
            {
                if (record.Structure != null)
                {
                    UndoStructure(record.Structure);
                }
                else
                {
                    SnapshotCapture.Restore(_resolver, record.Before);
                }

                record.Undone = true;
                return UndoOutcome.Success($"已撤销：{record.Summary}", undone: true);
            }
            catch (ToolException ex)
            {
                return UndoOutcome.Failure(ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error($"撤销 {record.ToolName} 失败", ex);
                return UndoOutcome.Failure("UNDO_FAILED", "撤销失败：" + Unwrap(ex).Message);
            }
        }

        /// <summary>恢复一条已撤销的记录。</summary>
        internal UndoOutcome Redo(string id)
        {
            var record = Find(id);
            if (record == null)
            {
                return UndoOutcome.Failure("NOT_FOUND", "找不到该操作记录，可能已超出保留范围。");
            }

            if (!record.Undone)
            {
                return UndoOutcome.Failure("NOT_UNDONE", "该操作尚未撤销，无需恢复。");
            }

            try
            {
                if (record.Structure != null)
                {
                    RedoStructure(record.Structure, record.ArgumentsJson);
                }
                else
                {
                    SnapshotCapture.Restore(_resolver, record.After);
                }

                record.Undone = false;
                return UndoOutcome.Success($"已恢复：{record.Summary}", undone: false);
            }
            catch (ToolException ex)
            {
                return UndoOutcome.Failure(ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error($"恢复 {record.ToolName} 失败", ex);
                return UndoOutcome.Failure("REDO_FAILED", "恢复失败：" + Unwrap(ex).Message);
            }
        }

        private void UndoStructure(StructureAction action)
        {
            switch (action.Kind)
            {
                case StructureKind.AddedWorksheet:
                    DeleteWorksheet(action.Name);
                    break;

                case StructureKind.RenamedWorksheet:
                    RenameWorksheet(action.Name, action.PreviousName);
                    break;

                case StructureKind.CreatedTable:
                    // 只解除表格转换，保留数据：删掉数据不是用户撤销「建表」的预期。
                    UnlistTable(action.SheetName, action.Name);
                    break;

                case StructureKind.CreatedChart:
                    DeleteChart(action.SheetName, action.Name);
                    break;

                default:
                    throw new ToolException("UNSUPPORTED", "该操作不支持撤销。");
            }
        }

        private void RedoStructure(StructureAction action, string argumentsJson)
        {
            // 结构类操作的恢复靠重放原始调用：它们没有「后快照」可还原。
            var args = string.IsNullOrWhiteSpace(argumentsJson)
                ? new JObject()
                : JObject.Parse(argumentsJson);

            switch (action.Kind)
            {
                case StructureKind.AddedWorksheet:
                    AddWorksheet(action.Name, action.AfterSheet);
                    break;

                case StructureKind.RenamedWorksheet:
                    RenameWorksheet(action.PreviousName, action.Name);
                    break;

                case StructureKind.CreatedTable:
                    RelistTable(action.SheetName, args);
                    break;

                case StructureKind.CreatedChart:
                    throw new ToolException(
                        "UNSUPPORTED",
                        "图表删除后无法自动恢复，请让我重新创建。");

                default:
                    throw new ToolException("UNSUPPORTED", "该操作不支持恢复。");
            }
        }

        private void DeleteWorksheet(string name)
        {
            object sheet = null;
            try
            {
                sheet = _resolver.ResolveWorksheet(name);

                // 关掉确认对话框：否则删除会弹窗阻塞在宿主 UI 线程上。
                var previous = Com.TryGet(Application, "DisplayAlerts", out var raw) ? raw : null;
                try
                {
                    Com.Set(Application, "DisplayAlerts", false);
                    Com.Call(sheet, "Delete");
                }
                finally
                {
                    if (previous != null)
                    {
                        Com.Set(Application, "DisplayAlerts", previous);
                    }
                }
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        private void AddWorksheet(string name, string afterSheet)
        {
            object workbook = null;
            object sheets = null;
            object created = null;
            object anchor = null;
            try
            {
                workbook = Com.Get(Application, "ActiveWorkbook")
                    ?? throw new ToolException("NO_WORKBOOK", "当前没有打开的工作簿。");
                sheets = Com.Get(workbook, "Worksheets");

                if (!string.IsNullOrWhiteSpace(afterSheet))
                {
                    anchor = _resolver.ResolveWorksheet(afterSheet);
                    created = Com.Call(sheets, "Add", Type.Missing, anchor);
                }
                else
                {
                    var count = Convert.ToInt32(Com.Get(sheets, "Count"));
                    anchor = Com.Get(sheets, "Item", count);
                    created = Com.Call(sheets, "Add", Type.Missing, anchor);
                }

                Com.Set(created, "Name", name);
            }
            finally
            {
                Com.Release(anchor);
                Com.Release(created);
                Com.Release(sheets);
                Com.Release(workbook);
            }
        }

        private void RenameWorksheet(string from, string to)
        {
            object sheet = null;
            try
            {
                sheet = _resolver.ResolveWorksheet(from);
                Com.Set(sheet, "Name", to);
            }
            finally
            {
                Com.Release(sheet);
            }
        }

        private void UnlistTable(string sheetName, string tableName)
        {
            object sheet = null;
            object listObjects = null;
            object table = null;
            try
            {
                sheet = _resolver.ResolveWorksheet(sheetName);
                listObjects = Com.Get(sheet, "ListObjects");
                table = Com.Get(listObjects, "Item", tableName);
                // Unlist 解除表格化但保留单元格数据。
                Com.Call(table, "Unlist");
            }
            catch (Exception ex)
            {
                throw new ToolException("TABLE_NOT_FOUND", $"找不到表格「{tableName}」：{Unwrap(ex).Message}");
            }
            finally
            {
                Com.Release(table);
                Com.Release(listObjects);
                Com.Release(sheet);
            }
        }

        private void RelistTable(string sheetName, JObject args)
        {
            var address = args.Value<string>("range");
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ToolException("ARG_MISSING", "缺少原始范围，无法恢复表格。");
            }

            var hasHeader = args.Value<bool?>("has_header") ?? true;
            var name = args.Value<string>("name");

            using (var range = _resolver.Resolve(address, sheetName))
            {
                object listObjects = null;
                object table = null;
                try
                {
                    listObjects = Com.Get(range.Worksheet, "ListObjects");
                    table = Com.Call(listObjects, "Add", 1, range.Range, Type.Missing, hasHeader ? 1 : 2);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        try
                        {
                            Com.Set(table, "Name", name);
                        }
                        catch
                        {
                            // 名称冲突不影响恢复本身。
                        }
                    }
                }
                finally
                {
                    Com.Release(table);
                    Com.Release(listObjects);
                }
            }
        }

        private void DeleteChart(string sheetName, string chartName)
        {
            object sheet = null;
            object shapes = null;
            object shape = null;
            try
            {
                sheet = _resolver.ResolveWorksheet(sheetName);
                shapes = Com.Get(sheet, "Shapes");
                shape = Com.Get(shapes, "Item", chartName);
                Com.Call(shape, "Delete");
            }
            catch (Exception ex)
            {
                throw new ToolException("CHART_NOT_FOUND", $"找不到图表「{chartName}」：{Unwrap(ex).Message}");
            }
            finally
            {
                Com.Release(shape);
                Com.Release(shapes);
                Com.Release(sheet);
            }
        }

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
    }
}
