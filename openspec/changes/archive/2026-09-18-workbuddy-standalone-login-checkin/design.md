# 设计

用户在当前任务中已确认独立组件登录与每日自动签到方案，本变更直接按已确认范围实施。

## 官方依据

- 官方 Windows 安装入口：https://www.codebuddy.cn/cli/install.ps1 。官方脚本下载原生包并校验 SHA256，然后调用原生 `install`。
- 官方 CLI ACP 源码：`initialize.authMethods`、`authenticate(methodId: internal)`、`_codebuddy.ai/authUrl`、`_codebuddy.ai/getUserInfo`。登录由官方认证管理器打开浏览器并保存凭据。
- 已安装官方 WorkBuddy `main/stdio-mcp-inspector.js`：国内服务为 `https://copilot.tencent.com`；POST `/v2/billing/meter/checkin-activity-status` 查询，POST `/v2/billing/meter/daily-checkin` 领取。响应使用 `data.active`、`data.today_checked_in`。

## 边界与失败处理

聊天继续由 ACP 处理。签到时只通过官方 ACP 接口获取当前会话身份，在后端内存使用 accessToken 向固定的官方 HTTPS 服务请求；不读取、解密、复制凭据文件，不记录原始 ACP/HTTP 输出，不把认证值传入面板或持久化。海外、企业或未知身份不擅自使用国内个人签到接口。不得伪造设备认证；若官方服务拒绝，显示无法确认或需要官方验证。

独立组件存在时优先使用原生可执行文件；兼容现有桌面端 CLI。安装与登录均显式点击触发，取消只终止本次创建的进程。官方登录可能切换当前账号，设置页提供明确文案。

签到缓存只有账号指纹、北京时间日期、请求是否提交及安全状态。跨进程文件锁覆盖读缓存、查询、提交与回查，防止多个面板重复领取。提交前先记录尝试；超时后仅回查，不自动重发领取。同日已有确定状态复用，跨日重新检查；未知状态可手动刷新。日期变化中的响应不可显示成新一天已签到。

前端启动和北京时间跨日时请求刷新，后台根据保存的授权模式决定是否检查。登录完成强制刷新账号、模型与签到；失败不影响已能使用的其他接入模式。
