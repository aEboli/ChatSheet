# WorkBuddy 授权 Specification

## Purpose

让 ChatSheet 使用 WorkBuddy 自己维护的当前登录态查看可用模型，同时保持授权凭据不出
WorkBuddy 的安全边界，并避免未实现的 ACP 对话能力伪装成普通 API。

## ADDED Requirements

### Requirement: 授权状态来自 WorkBuddy ACP

授权模式 SHALL 通过 WorkBuddy CLI 的 ACP `initialize` 与 `session/new` 检查当前登录态，
而 SHALL NOT 读取、解密或复制 WorkBuddy 凭据文件。

#### Scenario: 当前账号已登录

- **WHEN** WorkBuddy CLI 可启动且 ACP `session/new` 成功
- **THEN** 设置通道返回 `authorization.status` 为 `authorized`
- **AND THEN** 返回结果不包含 token、credential 或原始 CLI 输出

#### Scenario: 当前账号未登录

- **WHEN** ACP 返回 `auth_required` 或等价的未授权错误
- **THEN** 设置通道返回 `authorization.status` 为 `unauthorized`
- **AND THEN** 面板显示需要先在 WorkBuddy 完成登录的可操作提示

#### Scenario: WorkBuddy 不可用

- **WHEN** CLI 或 Node 不存在、启动失败、协议失败或 ACP 超时
- **THEN** 状态为 `unavailable`
- **AND THEN** 原始 stderr 和认证值不进入面板或日志

### Requirement: 模型目录使用 ACP 返回值

授权模式的 `models.list` SHALL 返回当前账号 ACP 会话中的模型目录，不得硬编码模型名单。
每个条目 SHALL 使用 `modelId` 作为稳定选择值，并可携带非敏感的名称与能力元数据。

#### Scenario: 读取多个可用模型

- **WHEN** ACP 返回 `models.availableModels`
- **THEN** 面板获得每个模型的 ID 和显示名称
- **AND THEN** 选择模型时保存 ID 而不是显示名称

#### Scenario: 模型目录为空

- **WHEN** 授权成功但 ACP 返回空模型目录
- **THEN** 通道返回授权成功状态和空目录
- **AND THEN** 面板允许重试并明确显示没有可选择模型

### Requirement: 授权模式不进入普通 API 对话链路

在 ACP 对话桥接实现前，授权模式 SHALL 不能创建普通 `ResolvedConnection` 或调用
`ChatClient`。发送请求 SHALL 返回稳定的 `AUTHORIZED_CHAT_NOT_SUPPORTED` 错误。

#### Scenario: 用户尝试发送授权模式对话

- **WHEN** 当前模式为 `Authorized` 且用户发送消息
- **THEN** 请求以 `AUTHORIZED_CHAT_NOT_SUPPORTED` 结束
- **AND THEN** 面板显示授权目录已接入但对话发送尚未接入的明确提示
- **AND THEN** 不发送普通 API 请求

### Requirement: 授权凭据不进入 ChatSheet 存储

授权模式 SHALL 不使用 ChatSheet 的自定义 API `SecretStore` 键保存 WorkBuddy token，
且明文 `settings.json` SHALL 不新增 WorkBuddy token 字段。

#### Scenario: 保存授权设置

- **WHEN** 用户保存授权模式与已选模型
- **THEN** 设置中只保存模式、模型 ID 和普通行为配置
- **AND THEN** WorkBuddy token 不出现在保存 payload、`settings.json` 或 ChatSheet secret store
