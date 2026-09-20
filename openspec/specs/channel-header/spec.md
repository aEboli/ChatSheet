# channel-header Specification

## Purpose
让用户在面板顶部直接确认当前已保存的实际接入渠道，并在切换连接或账号后及时看到更新。界面只展示经过白名单筛选的非敏感名称，不暴露凭据、账号 ID 或提供器原始响应。

## Requirements

### Requirement: 顶部展示当前渠道

系统 SHALL 在顶部导航图标之前展示已保存连接的渠道信息，使用纯文本渲染。

#### Scenario: 本机 CLI

- **WHEN** 当前实际连接来自 Codex CLI 且服务商名称为 Moon Stars
- **THEN** 显示 `codex cli · Moon Stars`
- **AND** 名称来自当前选中的服务商配置，不硬编码示例值；缺少名称时回退服务商标识，再不可得时仅显示 CLI 名称

#### Scenario: 自定义接口

- **WHEN** 当前已保存连接是自定义 API
- **THEN** 显示 `DIY`

#### Scenario: WorkBuddy 用户名

- **WHEN** 当前已保存连接是 WorkBuddy
- **THEN** 显示官方返回的当前用户名，无法取得时显示 `WorkBuddy`
- **AND** 不将账号 ID 当作用户名，不向页面传递凭据或原始账号响应

#### Scenario: 更新与布局

- **WHEN** 保存连接切换或当前账号状态发生更新
- **THEN** 标题栏更新为对应渠道，未保存的设置草稿不改变显示
- **AND** 长文本在窄面板中省略，悬停与读屏可取得完整文本，导航图标保持可用
