# 验证记录

日期：2026-09-20（北京时间）。修复版本：`0.10.3.8`。

## 实机依据与原因

- 国内 WorkBuddy 5.5.6 官方客户端的 `main/stdio-mcp-inspector.js` 使用 POST `/v2/billing/meter/checkin-activity-status` 查询、POST `/v2/billing/meter/daily-checkin` 领取，和本项目现有地址一致。
- 修复前，本机当日状态为 `unknown`、`Attempted=false`。强制刷新走完查询、领取、回查后变为 `checked_in`、`Attempted=true`，官方确认今日已签到。未读取凭据文件，也未记录账号、令牌或原始接口响应。
- 旧代码对任何同日结果直接返回缓存，网页定时器也只处理跨日，因此未知结果不会自行恢复。最初产生未知状态的具体服务端或网络原因无法从旧日志还原。

## 先复现，再修复

- 增加回归测试后，旧实现后端出现 2 项失败：未知缓存阻塞当天领取、已提交领取后无法自动回查迟到成功。
- 旧实现前端出现 5 项失败，覆盖同日恢复、消息桥首次失败、官方账号恢复以及等待中的重查调度。
- 修复仅调整完成缓存的判定与面板重查调度，原有跨进程锁、领取前落盘和已尝试标识继续生效。

## 验证结果

| 验证 | 结果 |
| --- | --- |
| `dotnet build ChatSheet.sln -c Release --nologo` | 通过，0 警告、0 错误，程序集版本为 0.10.3.8 |
| `ChatSheet.ToolTests.exe --workbuddy-account-tests` | 39 项通过，含网络恢复、并发单次领取、延迟确认、日期与国际版边界 |
| `node tests/web/workbuddy-checkin.test.mjs` | 14 项通过，使用真实消息桥模块与可控时钟验证五分钟调度 |
| `node tests/web/workbuddy-authorization.test.mjs` | 18 项通过 |
| `node tests/web/version.test.mjs` | 7 项通过 |
| `ChatSheet.ToolTests.exe --workbuddy-account-live` | 修复产物再次回查官方状态为 `checked_in`，日期 2026-09-20 |
| `ChatSheet.ToolTests.exe --workbuddy-account-live-intl` | 返回预期 `unsupported`，不进入国内签到接口 |
| UTF-8 严格解码、替换字符与中文人工复核 | 通过 |
| `openspec validate fix-workbuddy-auto-checkin-recovery --strict --no-interactive` | 通过 |

五分钟恢复通过可控时钟验证；没有让实机等待下一个自然日，也未修改本机系统时间或伪造官方设备验证。

## 部署状态

用户保存并退出 WPS 后，运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Action install -SkipBuild` 安装成功。已安装程序集版本为 `0.10.3.8`，DLL、签到脚本和静态页面的 SHA256 均与构建产物一致；32/64 位 COM 注册、Excel/WPS 加载项登记及运行时依赖自检通过。签到缓存保留 `checked_in`，未修改用户表格、账号或模型设置。
