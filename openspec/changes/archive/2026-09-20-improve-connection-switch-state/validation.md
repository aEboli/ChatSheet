# 验证记录

日期：2026-09-20（北京时间）。修复与安装版本：`0.10.3.9`。

## 目标与范围

四种接入模式初次进入和切换后自动获取状态与模型；当前面板按连接恢复模型选择，WorkBuddy 复用官方已有登录；状态、操作按钮与签到信息紧凑展示。切换预览不保存设置，明确保存才更新实际对话连接。

原任务已完成主要实现。本次恢复工作时，29 项切换回归均通过；额外补测保存返回 `unavailable` 的路径，复现了模型目录被清空的问题。修复为保留同一连接的目录并标记待确认；`unauthorized` 仍清空模型、目录并隐藏空列表。新增场景修复前失败、修复后通过。

## 自动验证

| 验证 | 结果 |
| --- | --- |
| 全部 `tests/web/*.test.mjs` | 33 个文件、1075 项检查通过，包含切换 30 项、模型目录 27 项、自动签到 14 项 |
| `dotnet build ChatSheet.sln -c Release --nologo` | 通过，0 警告、0 错误，程序集版本 0.10.3.9 |
| `ChatSheet.ToolTests.exe --workbuddy-tests` | 6 项 ACP 协议、会话、取消与未登录检查通过 |
| `ChatSheet.ToolTests.exe --workbuddy-account-tests` | 39 项通过，含签到恢复、并发单次领取、产品路径与进程环境隔离 |
| 既有 ProviderTests 定向调用 | WorkBuddy 目录、连接解析和模型绑定 23 项通过 |
| `ChatSheet.PaneHarness.exe --workbuddy-ui --width 360 --capture ...` | 真实 WebView2 视口 360px，24 项通过 |
| 同上，`--width 784` | 真实 WebView2 视口 784px，24 项通过 |
| UTF-8 严格解码与异常字符检查 | 代码、规范、版本及新增中文文案通过，已人工复核中文截图 |
| `git -c core.autocrlf=false -c core.whitespace=cr-at-eol diff --check -- ...` | 本次相关文件通过，保留项目已有 Windows 换行 |
| `openspec validate improve-connection-switch-state --strict --no-interactive` | 通过 |
| 归档后正式规范严格校验 | `connection-mode-refresh`、`workbuddy-authorization`、`workbuddy-settings-panel` 均通过 |

定向 ProviderTests 通过本地验证脚本反射调用既有三个方法，不启动 Excel，不发送模型对话。脚本与前端汇总位于 `artifacts/connection-switch-0.10.3.9/`。

## 真实界面与官方登录复用

- 国内模式自动显示已授权及 18 个模型，国际模式自动显示已授权及 21 个模型。
- 两种模式分别选定模型后立即来回切换，恢复各自的选择与目录；过程中登录请求和保存请求均为 0。
- 账号按钮位于状态右侧，按钮文字不换行、不拉伸填满空白；组件与签到使用内容宽度，两个实际视口均无横向溢出。截图已人工查看。
- 旧界面测试假定切回模式会清空授权，现按状态恢复后的行为更新：保留已授权状态时，用户明确点击“切换账号”仍请求重新授权。这类点击通过消息桥 fixture 拦截，没有实际切换或退出用户账号。
- 模式切换及模型目录使用真实消息桥和官方 ACP；失败、迟到响应、未授权和密钥草稿使用内存 fixture 验证。没有为本次验证发送付费模型对话。
- 验证前后以及安装后的用户设置文件 SHA256 一致。

截图和详细记录：

- `artifacts/connection-switch-0.10.3.9/ui-360.log`
- `artifacts/connection-switch-0.10.3.9/ui-784.log`
- `artifacts/connection-switch-0.10.3.9/workbuddy-360-Authorized-settings.png`
- `artifacts/connection-switch-0.10.3.9/workbuddy-784-AuthorizedInternational-settings.png`
- `artifacts/connection-switch-0.10.3.9/frontend-tests.log`
- `artifacts/connection-switch-0.10.3.9/install-verification.log`

## 安装

安装前确认目标为 `%LOCALAPPDATA%\ChatSheet\app`，父目录及目标均非重解析点，旧 DLL 未被占用。执行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Action install -SkipBuild` 成功。

安装版本为 `0.10.3.9`，DLL 和全部 Web 资源共 17 个文件的 SHA256 与构建产物一致；32/64 位 COM 注册、Excel/WPS 加载项登记、WebView2 与 .NET Framework 运行时自检通过。本次未发布 GitHub Release，未操作用户工作簿；在重新打开的 WPS/Excel 内由用户使用，界面验证由独立真实 WebView2 宿主完成。

变更已归档至 `openspec/changes/archive/2026-09-20-improve-connection-switch-state/`，相关正式规范已同步。
