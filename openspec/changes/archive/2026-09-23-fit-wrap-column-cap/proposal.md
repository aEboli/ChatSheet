## Why

“适配当前表”按内容自动调整列宽时，长文本会把列拉得过宽，表格难以浏览。当前表格需要一个稳定的宽度上限，并在压缩后让长文本换行。

## What Changes

- `fit_range` 自动调整列宽后，将超过 100 个 Excel 列宽单位的列设置为 100。
- `fit_range` 对目标范围启用自动换行，并在列宽限制生效后重新调整行高。
- 适配操作的撤销快照同时保存和恢复原始换行状态。

## Capabilities

### Modified Capabilities

- `spreadsheet-operation-access`: 适配当前表的列宽上限和自动换行行为。

## Impact

- `ToolExecutor.Structure.cs` 的 `fit_range` 执行顺序和结果摘要。
- 适配撤销快照中的对齐状态。
- 表格工具回归测试和 `spreadsheet-operation-access` 规范。
