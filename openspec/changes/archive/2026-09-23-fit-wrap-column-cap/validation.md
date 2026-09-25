## 已完成验证

- `dotnet build src/ChatSheet.AddIn/ChatSheet.AddIn.csproj --no-restore`：通过。
- `node --test tests/web/fit-entry.test.mjs tests/web/fit-card.test.mjs tests/web/range-label.test.mjs tests/web/imports.test.mjs`：通过，115 项断言通过。
- `openspec validate 2026-09-23-fit-wrap-column-cap --type change`：通过。
- 真实 Excel/WPS COM 回归未能运行：当前环境缺少 .NET Framework 4.8 引用程序集，完整工具测试项目无法构建。
