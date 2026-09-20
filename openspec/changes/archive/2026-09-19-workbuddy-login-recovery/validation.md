# 验证

- `dotnet build src/ChatSheet.AddIn/ChatSheet.AddIn.csproj --configuration Release --nologo`：通过，0 警告、0 错误。
- 网页回归（`tests/web/*.test.mjs`）：通过。
- `--workbuddy-tests` 与 `--workbuddy-account-tests`：通过。
- 国际版实机模型探测：已授权，读取 16 个模型。
- PaneHarness 国际版 360px：0 失败，授权按钮、模型目录和窄面板布局通过。
- 安装后程序集版本为 `0.10.3.5`，32/64 位 COM 注册及 Excel `LoadBehavior=3` 正常。
- 未进行真实鼠标点击 Google 登录的视觉验证：当前 Computer Use 会话没有可用的 Codex auth token；授权地址由 ACP 官方回调、域名校验、系统浏览器打开和面板重开入口覆盖。
