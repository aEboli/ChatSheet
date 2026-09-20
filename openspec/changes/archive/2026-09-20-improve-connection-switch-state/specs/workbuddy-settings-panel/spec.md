## MODIFIED Requirements

### Requirement: WorkBuddy 状态使用单一紧凑状态区

WorkBuddy 模式 SHALL 在设置页使用一个授权状态区，显示授权状态、ACP 可用性和当前模式允许的动作；状态摘要与主要操作 SHALL 并排呈现，签到和组件信息 SHALL 使用内容宽度的辅助展示，不占用整行空白。加载、已连接、未登录和连接待确认 SHALL 可区分，同一状态 SHALL NOT 同时以顶部就绪条、重复错误文本和授权说明段落展示。

#### Scenario: 国际版组件可用但未登录

- **WHEN** 当前模式为 `AuthorizedInternational`，ACP 路径可用且账号未授权
- **THEN** 面板显示“未授权”和可用的 Google/GitHub 登录入口
- **AND THEN** 不显示“未找到组件”的重复说明
- **AND THEN** 国内每日签到显示为紧凑的不可用提示，而不是铺满整行的状态条

#### Scenario: 组件不可用

- **WHEN** 当前模式的 ACP 路径不可用
- **THEN** 面板显示短状态及可执行的安装/登录动作
- **AND THEN** 详细原因只通过控件的可访问名称、悬停提示或用户主动展开的提示提供

#### Scenario: 宽窄面板布局

- **WHEN** 在 360px 窄面板或 784px 宽面板显示授权区
- **THEN** 主要按钮位于状态摘要右侧，必要时信息自然分行
- **AND THEN** 按钮文字不换行、没有横向溢出、单个按钮不会拉伸填满剩余空间

#### Scenario: 正在自动恢复

- **WHEN** 刚切换模式并等待当前请求
- **THEN** 显示正在连接或确认中，不把尚未返回当作组件不可用
- **AND THEN** 已有目录时保持可见并明确其正在核验
