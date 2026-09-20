# 验证记录

验证日期：2026-09-18。

- `openspec validate --all --strict`：10 项规格通过。
- `dotnet build ChatSheet.sln --configuration Release --no-restore`：0 警告、0 错误。
- `tests/web/*.test.mjs`：30 个套件全部通过。
- `ChatSheet.PaneHarness --workbuddy-ui --width 360`：9 项通过、0 失败；无 ready banner、无常驻 field-hint、无横向溢出。
- ACP 进程夹具：6 项通过；账号/签到与国内/国际路径测试：24 项通过。
- 国内 WorkBuddy 实机：目录 18 个模型，ACP 两轮对话、模型探测和临时 Excel 工具写入全部通过。
- 国际 WorkBuddy 实机：独立组件与 ACP 可用，当前账号未授权；命令以退出码 2 明确报告前置登录阻塞，未自动打开浏览器。
