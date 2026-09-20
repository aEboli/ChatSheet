## Why

用户已要求修复 WorkBuddy 授权模式无法发送的问题并安装更新。旧版只读取模型目录，发送仍被拦截；模型测试也没有使用 ACP。标题栏应按最新要求只显示阿拉伯数字，模型倍率应在设置页和对话页一致显示。

## What Changes

- 通过已安装 WorkBuddy 自带 CLI 的 ACP 发送和接收对话，选用用户保存的模型。
- 表格工具继续经过 ChatSheet 现有文本工具协议、审批、执行和撤销链路；支持工具结果续传与取消。
- 授权模式的模型测试使用同一 ACP 客户端。
- 共享模型目录保留安全的倍率字段，在对话选择器显示倍率。
- 标题栏仅显示 `0.10.2`，保留楷体；普通修复按补丁版本递增。

## Capabilities

### Modified Capabilities

- `workbuddy-authorization`: 将目录授权扩展为可执行的 ACP 对话与模型测试。

### New Capabilities

- `model-metadata-version-display`: 规范倍率和纯数字版本显示。

## Impact

影响接入客户端、AgentRunner、模型测试、面板目录缓存和标题栏。复用 WorkBuddy 当前登录态，不读取或复制授权凭据；无新增服务依赖。用户已授权本次修复与本地安装。
