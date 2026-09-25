# 验证

- `node --test tests/web/*.test.mjs`
- `dotnet build ChatSheet.sln --configuration Release --nologo`
- `tests\\ChatSheet.ToolTests\\bin\\Release\\ChatSheet.ToolTests.exe --word-tests`
- `tests\\ChatSheet.ToolTests\\bin\\Release\\ChatSheet.ToolTests.exe --workbuddy-tests`
- `tests\\ChatSheet.ToolTests\\bin\\Release\\ChatSheet.ToolTests.exe --reliability-tests`
- `openspec validate 2026-09-23-word-workbuddy-parity --strict --no-interactive`
- `host-ui-copy.test.mjs` 覆盖 Word/Excel 欢迎语和附件提示；Word 桥通过共享 `AgentChannels` 注册 `models.list` 与 `workbuddy.login`，由构建和现有 WorkBuddy ACP 回归覆盖其实现。
