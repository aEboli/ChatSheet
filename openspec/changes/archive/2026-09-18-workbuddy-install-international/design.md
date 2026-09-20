# 设计

## 模式

新增 `ConnectionMode.AuthorizedInternational`。`Authorized` 继续表示国内 WorkBuddy，优先选择 ACP `internal`（微信）授权；国际版选择 `external`（Google/GitHub）授权。两种模式均通过 WorkBuddy ACP 获取模型和聊天连接，且都标记为 `IsWorkBuddy`。

## 安装与登录

安装器从官方 release 清单读取版本和 SHA256，下载 `codebuddy-code-headless_Windows_x86_64.zip`，只接受其中的 `codebuddy-headless.exe`，然后执行官方 `install <version>`。安装完成仍通过 `Data/versions/<version>/codebuddy.exe` 检测真实入口。

运行时状态同时返回 `standalone` 和 `canLogin`。前者只控制是否显示安装按钮，后者由所有可用 ACP 路径决定登录按钮是否可点击。登录使用 `TryFindPaths()`，所以已有桌面端 ACP 时无需先安装独立组件。

## 国际授权与安全域名

授权连接接受国内官方域名以及 `codebuddy.ai`、`workbuddy.ai`（含 staging 子域名）的 HTTPS URL；继续拒绝 HTTP、用户信息、非标准端口和其他域名。国际版登录成功后刷新 ACP 模型目录，但不访问 `copilot.tencent.com` 签到接口，界面显示签到不适用。

## 缓存与设置

模式值自然进入 `ConnectionKey` 和前端模型目录键，确保国内/国际目录与收藏不混用。保存 payload 仍只包含模式、模型 ID 和普通设置，WorkBuddy token 不进入 ChatSheet 存储。
