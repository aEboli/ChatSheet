# WorkBuddy 独立组件安装与国际版授权

## Why

独立授权组件安装流程仍使用旧的 Windows 压缩包和入口文件，导致下载后无法解压或安装。设置页还把“独立组件已安装”误当成“可以发起浏览器授权”，在检测到桌面端 ACP 时会禁用“切换账号”。同时，国际版 WorkBuddy 的 ACP 授权方式和登录域名与国内版不同，当前界面没有可选入口。

## What Changes

- 使用官方当前 Windows headless 包 `codebuddy-code-headless_Windows_x86_64.zip`，解压 `codebuddy-headless.exe` 后执行官方 `install <version>`。
- 将可登录能力与独立组件安装状态分开；只要检测到可用 ACP，登录/切换账号按钮就可用，登录请求也可回退到桌面端 CLI。
- 新增“WorkBuddy 国际版授权登录”模式，使用 ACP 的 `external`（Google/GitHub）授权，允许官方 `.ai` 登录域名。
- 国内版和国际版使用不同的设置/模型/收藏连接键；国际版不调用固定的国内每日签到服务。

## Impact

涉及 WorkBuddy 运行时安装、ACP 账号连接、设置模式解析、对话客户端路由、前端设置页和模型目录缓存。授权凭据仍只由 WorkBuddy 管理，不进入 ChatSheet 设置、日志或面板消息。
