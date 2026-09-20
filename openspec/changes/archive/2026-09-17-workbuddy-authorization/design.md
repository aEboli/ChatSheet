# 设计：WorkBuddy 授权状态与模型目录

## 调用边界

`WorkBuddyProvider` 负责定位 WorkBuddy 自带 Node 与 CLI，启动一次短生命周期进程，使用
JSONL JSON-RPC 发送 `initialize` 和 `session/new`，读取会话返回的 `models.availableModels`。
进程使用 `--acp --no-session-persistence --tools ""`，不复用会话、不启用工具。

所有 stdout/stderr 只在 provider 内消费；非 JSON 行被丢弃，错误不会把原始输出写入日志、
面板或异常消息。返回对象只包含模型 ID、名称、能力布尔值和输入上限等安全元数据。

## 状态模型

provider 将结果分为 `authorized`、`unauthorized` 和 `unavailable`：

- `authorized`：ACP `session/new` 成功，且可解析当前模型目录。
- `unauthorized`：ACP 返回 `auth_required` 或明确的未登录错误。
- `unavailable`：找不到安装、进程启动失败、协议错误或超时。

设置通道将状态映射为脱敏 `authorization` payload。授权模式的 `readyDetail` 明确说明
模型目录状态和「普通对话发送尚未接入」，因此不会把授权状态误报成普通 API 已就绪。

## 连接与安全

`ConnectionMode.Authorized` 不生成 `ResolvedConnection`。`Settings.ResolveConnection()` 对该
模式抛出专用 `AUTHORIZED_CHAT_NOT_SUPPORTED`，阻止 `AgentRunner` 调用 `ChatClient`。
授权模式的 `ConnectionKey` 只含模式和 CLI 来源，不含令牌；`FavoritesKey` 不尝试读取
本机 API CLI 配置。WorkBuddy 令牌始终留在 WorkBuddy 自己的认证存储中。

## 面板协议

`models.list` 在授权模式下调用 WorkBuddy provider，返回 `protocol: workbuddy-acp`、
`authorization` 和对象数组 `models`。每个模型的 `modelId` 是保存与请求选择使用的值，
`name` 只用于显示。普通接口继续返回字符串数组；面板统一将两种结果归一化到模型目录
缓存，但设置页保留名称用于下拉展示。

设置页 `settings.get` 查询一次当前授权状态；「自动获取」仍通过现有 `models.list` 通道
刷新目录。只读的授权状态和模型元数据不写回明文设置。

## 超时与错误

ACP provider 使用明确的总超时并在超时、退出和异常时回收子进程。未授权、未安装、超时
和协议失败使用稳定错误码；用户消息不包含 CLI 原始 stderr 或任何认证值。
