## MODIFIED Requirements

### Requirement: 授权模式不进入普通 API 对话链路

授权模式 SHALL 使用 WorkBuddy CLI ACP 完成对话和模型探测，而 SHALL NOT 将空 API 地址或 WorkBuddy 登录态交给普通 HTTP `ChatClient`。

#### Scenario: 用户尝试发送授权模式对话

- **WHEN** 当前模式为 `Authorized` 且用户发送消息
- **THEN** 通过 ACP 初始化并创建会话，选择用户保存的模型后发送 prompt
- **AND THEN** 将流式正文、思考和完成状态返回面板
- **AND THEN** 不再返回 `AUTHORIZED_CHAT_NOT_SUPPORTED`

#### Scenario: 表格工具续传

- **WHEN** 模型输出 ChatSheet 文本工具调用
- **THEN** 现有工具审批、执行和撤销链路处理调用
- **AND THEN** 工具结果发送到同一 ACP 会话并继续接收回答
- **AND THEN** 不重复发送先前助手输出

#### Scenario: 用户取消或请求超时

- **WHEN** 用户停止对话或 ACP 请求达到截止时间
- **THEN** 结束当前请求并释放本次创建的 CLI 进程
- **AND THEN** 用户取消和超时可区分，超时返回可操作的中文提示

#### Scenario: 授权模型探测

- **WHEN** 用户测试授权模式的模型
- **THEN** 使用 ACP 发送小型探测请求并根据流式结果判定可用性
- **AND THEN** 不创建普通 HTTP 请求

#### Scenario: 服务端请求额外工具权限

- **WHEN** ACP 服务端发出权限请求
- **THEN** 返回取消结果且不执行 WorkBuddy 工具
- **AND THEN** 不因字符串请求 ID 导致协议解析失败
