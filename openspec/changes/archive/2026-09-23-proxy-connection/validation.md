# 验证

- `dotnet build ChatSheet.sln --configuration Release --nologo`：通过，0 警告、0 错误。
- `node --test tests/web/*.test.mjs`：全部通过（包含多代理配置回归测试）。
- `dotnet run --project tests/ChatSheet.ToolTests/ChatSheet.ToolTests.csproj --configuration Release -- --reliability-tests`：26/26 通过。
- `git diff --check`：通过。
- `model-catalog.test.mjs` 覆盖 HTTP 与 SOCKS5 代理的目录隔离；`proxy-profiles.test.mjs` 覆盖多配置切换、页面顺序、地址/端口布局及密码覆盖语义。
- 密码回归检查：未编辑密码时请求不携带 `proxyPassword`，显式清空时携带空字符串并覆盖旧 DPAPI 密码，不会在保存前回退旧值。
