## ADDED Requirements

### Requirement: 国际桌面 ACP 继承产品模型上下文

检测到 WorkBuddyAI 桌面端时，国际授权模式 SHALL 优先使用其 CLI。当该产品配置目录具有会话产品快照时，模型查询和对话 SHALL 通过官方 `CODEBUDDY_HOST=workbuddy-desktop` 与 `ACC_PRODUCT_CONFIG_PATH` 传递最新快照；SHALL 继续使用 ACP 返回的模型目录与倍率，不硬编码名单。

#### Scenario: 桌面端有完整模型目录

- **WHEN** WorkBuddyAI 存在会话产品快照
- **THEN** 直接启动和 WPS 代理启动均传递最新快照路径及桌面宿主标识
- **AND THEN** 查询与对话使用相同产品上下文，保留 ACP 返回的零倍率模型

#### Scenario: 快照目录不可用

- **WHEN** 桌面快照目录不存在或无法读取
- **THEN** 保留原有 ACP 启动和真实目录结果，不伪造完整名单

#### Scenario: 独立国际组件

- **WHEN** 当前来源是独立 CodeBuddy CLI
- **THEN** 不为它注入 WorkBuddyAI 会话快照
