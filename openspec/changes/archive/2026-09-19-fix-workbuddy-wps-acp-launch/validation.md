# 验证

- `dotnet build ChatSheet.sln --configuration Release --nologo --no-incremental`：通过，0 警告、0 错误。
- x86 `--workbuddy-account-tests`：通过，包括 WMI ACP 双管道、Job Object 回收和路径桥接。
- `--workbuddy-tests`：通过，流式响应、权限请求、取消和释放均通过。
- `tests/web/*.test.mjs`：31 个文件全部通过；版本回退显示为 `0.10.3.6`。
- `ChatSheet.PaneHarness.exe --workbuddy-ui --width 360`：通过并生成设置页截图，国际版登录按钮可见且未授权状态明确。
- 真实 WPS x86（`et.exe`，WPS 12.0 Build 19823）：版本显示 `0.10.3.6`；真实 `#workbuddy-login.click()` 返回已点击；WMI 创建 ACP、加入宿主生命周期 Job、完成 `initialize`，收到 `external` 登录方式和 `www.codebuddy.ai` 授权页，并交给默认 Chrome。
- 真实 WPS 关闭后，本轮代理 PID `325484,327368` 均已退出；未发现本轮 `codebuddy.exe` 残留。
- 当前测试账号未完成第三方 Google/GitHub 登录，因此实机模型目录返回的是明确的 `WORKBUDDY_NOT_AUTHORIZED`；没有代填密码、验证码或提交第三方授权。授权完成后的模型刷新由 `RunWorkBuddyActionAsync` 的强制 `models` 探测、前端目录回写和 x86 ACP 回归覆盖。
