## MODIFIED Requirements

### Requirement: 适配当前表限制宽列并启用换行
`fit_range` SHALL 在按内容自动调整列宽后，将目标范围内超过 100 个 Excel 列宽单位的列设置为 100；随后 SHALL 对目标范围启用自动换行并重新自动调整行高。独立的 `autofit_range` 工具不受此上限约束。

#### Scenario: 长文本列被限制并换行
- **WHEN** 用户对包含长文本的范围执行 `fit_range`
- **THEN** 自动调整后任何超过 100 的目标列宽均为 100
- **AND THEN** 目标范围的 `WrapText` 为真
- **AND THEN** 行高按限制后的列宽重新调整，换行内容可见

#### Scenario: 适配可撤销换行变更
- **WHEN** `fit_range` 修改了目标范围的列宽或换行状态且快照完整
- **THEN** 撤销同时恢复原始列宽、行高、水平/垂直对齐和换行状态
