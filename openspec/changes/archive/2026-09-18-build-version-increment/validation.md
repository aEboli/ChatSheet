# 版本构建号验证记录

验证日期：2026-09-18。

## 结果

- `dotnet build ChatSheet.sln -c Release --nologo`：0 警告、0 错误。
- 标题栏版本测试：7 项通过，覆盖三段版本与四段构建号显示。
- WorkBuddy 授权 Web 测试：15 项通过。
- ACP 对话回归：6 项通过。
- WorkBuddy 账号/签到测试：0 失败。
- 面板宿主回归：国际版 ACP 已授权并读取 21 个模型，标题栏显示 `0.10.3.1`。
- `openspec validate --all --strict --no-interactive`：11 项通过。
- 已重新安装并注册 `ChatSheet.AddIn, Version=0.10.3.1`；COM 两个位数视图和 Excel `LoadBehavior=3` 正常。
- `git diff --check`：通过。
