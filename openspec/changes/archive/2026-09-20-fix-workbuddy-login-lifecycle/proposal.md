# 修复 WorkBuddy 切换账号与登录完成刷新

## Why

用户反馈点击切换账号会连带退出国际版桌面客户端，授权成功后面板仍等待，并同时打开两个浏览器窗口。官方本机组件源码确认 authenticate 会先 logout，认证存储由产品共享；CliExternalUriOpener 会自行打开授权链接，面板收到通知后再次打开导致重复。

## What Changes

- 已有账号的切换交给当前来源的官方客户端；ChatSheet 不调用退出式授权。
- 首次登录必须在跨进程独占锁内确认同一来源明确未授权且 getUserInfo 无用户；异常不触发重新授权。
- 官方 CLI 负责首次开窗，面板仅提供当前操作内的手动重开入口。
- 等待期间只读检查同一来源的授权，成功立即返回其模型；返回面板时刷新，失败或取消保留原状态。
- 使用 operationId 绑定通知、重开及取消；操作结束后废弃链接。

## Capabilities

### Modified Capabilities

- workbuddy-authorization：修复现有切换账号、浏览器授权与完成刷新。

## Impact

仅修改登录流程、相关设置页及定向回归。不实施既有 workbuddy-session-safety 提案中的标题栏身份与持久化来源功能。用户本次明确要求修复上述两个故障，按此范围实施。
