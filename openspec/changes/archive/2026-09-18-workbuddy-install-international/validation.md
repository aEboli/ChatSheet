# WorkBuddy 安装与国际版验证记录

验证日期：2026-09-18。

## 结果

- 官方 release 清单当前版本为 `2.154.0`；校验清单包含 `codebuddy-code-headless_Windows_x86_64.zip`，其入口为 `codebuddy-headless.exe`。
- Release 构建：0 警告、0 错误。
- 全量 Web 测试：0 失败；新增授权测试覆盖 15 项，包括国际模式缓存隔离、`external` 语义和桌面端 ACP 下切换账号可用性。
- ACP 进程 fixture：6 项通过。
- WorkBuddy 账号/签到测试：21 项通过，覆盖 headless 包名、国际域名白名单、国际版不调用国内签到和跨进程签到互斥。
- 完整 Excel COM 工具测试：730 项通过、0 失败；接入层新增国际版连接解析测试通过。
- `git diff --check` 通过。

## 安全边界

国际版只允许官方 `codebuddy.ai`/`workbuddy.ai` HTTPS 授权地址，凭据仍由 WorkBuddy ACP 管理；国际模式不请求国内 `copilot.tencent.com` 签到接口。设置文件、模型缓存和前端 payload 不保存 WorkBuddy token。
