# 提案：接入 WorkBuddy 授权状态与模型目录

## Why

设置页已有「授权登录」选项，但后端仍把它当作未实现模式。WorkBuddy 已提供本机 CLI
的 ACP 接口，并能在创建会话时返回当前登录账号可用的模型目录；ChatSheet 需要复用
WorkBuddy 自己维护的登录态，而不是读取或复制其凭据。

## What Changes

- 通过 WorkBuddy CLI ACP 判断当前登录态，并向面板返回脱敏的授权状态。
- 通过 ACP 返回当前账号的模型 ID、显示名称和能力元数据，让用户可以选择模型。
- 授权模式的设置保存不再被「未实现」拦截。
- 明确阻止授权模型进入现有普通 API 对话链路，直到 ACP 对话桥接另行实现。

## Non-Goals

- 不解析 `.credentials.json`、`models.json` 或任何加密授权文件。
- 不把 WorkBuddy OAuth/token 写入 ChatSheet 的设置或 `SecretStore`。
- 不硬编码 WorkBuddy 模型名单、私有 HTTP 对话端点或账号信息。
- 本期不实现 ACP `session/prompt` 对话转发、工具代理或流式回复。

## Capabilities

### New Capabilities

- `workbuddy-authorization`: 读取 WorkBuddy 授权状态与当前账号可用模型目录。

## Impact

新增 WorkBuddy ACP provider；修改设置解析、设置与模型列表通道、普通对话入口和设置页
模型选择。增加 provider 解析与错误映射测试，以及授权模式的前端回归测试。
