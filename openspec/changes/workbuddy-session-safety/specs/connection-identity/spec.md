## ADDED Requirements

### Requirement: 标题栏展示实际连接

系统 SHALL 在标题栏导航图标前显示已保存接入方式的实际渠道；官方 ACP 返回账号 ID 时 SHALL 一并显示，仅允许白名单身份字段进入 UI。

#### Scenario: 授权渠道

- **WHEN** WorkBuddy ACP 返回 userId
- **THEN** 标题栏显示国内或国际渠道与 ID，悬停显示来源和验证状态
- **AND THEN** token、accessToken、原始账号响应不得进入 UI、日志或 ChatSheet 存储

#### Scenario: 没有账号 ID

- **WHEN** 当前为自定义 API、本机 CLI 或官方未返回 ID
- **THEN** 只显示可确定的实际渠道，不推测身份

#### Scenario: 窄面板与未保存设置

- **WHEN** 面板变窄或用户更改尚未保存的接入模式
- **THEN** 渠道文本可省略且完整内容能通过悬停或读屏取得，导航按钮不被挤出
- **AND THEN** 标题栏仍展示已保存的实际渠道
