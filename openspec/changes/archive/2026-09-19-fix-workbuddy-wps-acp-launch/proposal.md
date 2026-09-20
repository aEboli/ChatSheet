# 修复 WPS 中 WorkBuddy ACP 启动

## Why

WPS x86 能加载 ChatSheet，但其宿主进程拒绝 `CreateProcess`。因此同一份国际版 CLI 在普通 x86 测试进程中可读取 16 个模型，在 WPS 内却被误报为组件不可用，点击 Google/GitHub 登录也无法进入浏览器授权。

## What Changes

- 为拒绝直接创建子进程的宿主增加 WMI 进程服务代理启动，并用随机命名管道保留 ACP 标准输入/输出。
- Excel、测试程序及允许创建子进程的宿主继续使用现有直接进程路径。
- 安装时为 WPS 可见目录创建官方 CLI 的硬链接桥接入口；不复制或修改官方二进制。
- 失败日志保留 WMI 启动与管道连接阶段，不记录授权 URL 查询参数、凭据或 ACP 原始正文。

## Capabilities

### Modified Capabilities

- `workbuddy-authorization`: 增加宿主禁止创建子进程时的 ACP 代理启动与安全传输要求。

## Impact

涉及 WorkBuddy 进程传输封装、模型探测、登录、对话、安装桥接和真实 WPS x86 回归测试；不新增常驻服务、凭据存储或第三方域名。
