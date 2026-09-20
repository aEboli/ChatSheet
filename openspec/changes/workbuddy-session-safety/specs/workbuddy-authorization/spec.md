## ADDED Requirements

### Requirement: 登录操作不得退出已有账号

ChatSheet SHALL 不在已有官方账号上执行会先登出的 authenticate。无法可靠隔离认证存储时 SHALL 将账号切换交给对应官方客户端。

#### Scenario: 已授权或暂时无法验证

- **WHEN** 用户点击登录但已有账号，或探测因网络、启动或协议异常失败
- **THEN** 不调用 authenticate，不清空原账号和模型
- **AND THEN** 已有账号可刷新，异常显示待验证和重试入口

#### Scenario: 首次登录

- **WHEN** 模型探测明确未授权，且登录进程 getUserInfo 明确没有用户
- **THEN** 在跨进程独占锁内允许 authenticate，取消或失败不影响其他来源的账号

#### Scenario: 授权页重开与操作结束

- **WHEN** 浏览器打开失败或用户需要重开授权页
- **THEN** ACP 等待保留，只有同一活动 operationId 能重开其授权页
- **AND THEN** 取消、超时或结束后旧链接失效，迟到通知不能覆盖新操作

### Requirement: 账号来源固定且异常不冒充过期

系统 SHALL 只持久化成功连接的产品来源标识，后续探测、对话与签到 SHALL 使用同一来源。来源失败 SHALL NOT 静默连接另一产品账号。

#### Scenario: 重启或组件失败

- **WHEN** 面板重启或当前来源暂时不可用
- **THEN** 继续查找已选来源；不可用时报告原因，不改用其他来源

#### Scenario: 短暂异常与明确过期

- **WHEN** 探测暂时失败
- **THEN** 保留同一来源上次模型和身份并标记待验证，不能以缓存冒充本次验证成功
- **AND THEN** 官方明确未授权时清除身份，不继续允许使用已失效授权
