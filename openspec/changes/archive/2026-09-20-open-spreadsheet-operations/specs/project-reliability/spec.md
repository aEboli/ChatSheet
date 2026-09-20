## MODIFIED Requirements

### Requirement: Range limits remain effective
工具 SHALL 用不会在完整工作表尺寸下溢出的计数描述范围；SHALL 根据操作采用分页、分块或宿主批量调用，不按固定单元格总数拒绝有效范围。多区域 SHALL 按操作语义处理，不得只处理首个区域或将不支持的语义静默改写。

#### Scenario: Full worksheet or multiple areas
- **WHEN** 工具收到完整工作表范围或多区域并集
- **THEN** 完整范围计数不溢出，读取分页面返回，支持的多区域操作覆盖全部目标；不支持的组合在修改前解释具体原因
