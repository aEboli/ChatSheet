## ADDED Requirements

### Requirement: Default automatic execution
系统 SHALL 在没有已保存审批策略时使用 Automatic，并保留用户显式选择 PerWrite 或 PerTurn 的能力；本次安装 SHALL 按用户的开放请求切换到 Automatic。

#### Scenario: 新用户执行写入和结构操作
- **WHEN** 默认设置下模型依次写入、清空及创建图表
- **THEN** 每项直接执行且返回结果卡片，不等待审批卡片

#### Scenario: 用户重新选择逐项审批
- **WHEN** 用户明确切回 PerWrite
- **THEN** 后续修改按原有逐项审批规则执行
