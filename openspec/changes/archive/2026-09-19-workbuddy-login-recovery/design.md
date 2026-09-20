# 设计

1. `workBuddyRuntime.available` 继续表示 ACP 路径探测结果；`canLogin` 表示设置页是否应提供授权动作，针对明确的 WorkBuddy 模式保持为真。无路径时点击动作由后端重新探测并返回安装或启动提示。
2. 前端登录按钮只在操作进行中禁用，不再把一次探测失败渲染成不可点击控件。国际版仍单独显示官方组件安装入口。
3. ACP 启动时为已分类的产品路径注入对应的 WorkBuddy 配置目录环境变量，使桌面端现有登录态由官方 CLI 使用；未知 PATH 入口不注入猜测目录。
4. 登录动作完成后继续强制刷新授权目录，并用当前模式校正模型选择。
5. 路径探测不依赖宿主进程位数：32 位 Excel/WPS 使用 `USERPROFILE` 回退解析用户目录，并额外检查 `ProgramW6432`；模型探测、对话和登录统一复用同一套带配置目录的 ACP 启动参数。
6. .NET Framework 宿主可能同时继承 `Path` 与 `PATH`，导致 `ProcessStartInfo.Environment` 初始化失败。启动器先按不区分大小写的键去重，并同步新版 `Environment` 与旧版 `environmentVariables` 存储，确保 Excel/WPS 实际创建子进程时仍携带完整环境和对应产品配置目录。

## 安全边界

只传递产品配置目录路径，不读取其中的认证内容；浏览器地址仍由 `WorkBuddyAccountConnection` 按模式做 HTTPS 官方域名校验。
