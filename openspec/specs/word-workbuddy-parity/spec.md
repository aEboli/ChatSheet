# word-workbuddy-parity Specification

## Purpose
TBD - created by archiving change 2026-09-23-word-workbuddy-parity. Update Purpose after archive.

## Requirements

### Requirement: Word 使用真实模型目录与 WorkBuddy 授权通道

Word/WPS Writer 面板 SHALL 使用与 Excel 相同的模型目录协议和 WorkBuddy ACP 授权协议。`models.list` SHALL 返回当前连接的真实目录与授权状态；`workbuddy.login`、`workbuddy.install`、`workbuddy.cancel`、`workbuddy.account` 和授权页重开通道 SHALL 可用，并 SHALL 继续把凭据留在 WorkBuddy。

#### Scenario: Word 获取模型

- **WHEN** Word 设置页选择本机 CLI、自定义接口或 WorkBuddy 模式并刷新模型
- **THEN** Word 桥接按当前未保存的连接配置返回模型目录
- **AND THEN** WorkBuddy 模式使用 ACP 返回的模型 ID，不返回固定空目录

#### Scenario: Word 完成 WorkBuddy 授权

- **WHEN** 用户在 Word 面板点击登录、安装、取消或重新打开授权页
- **THEN** Word 桥接返回与 Excel 相同的状态、进度和授权链接事件
- **AND THEN** Word 面板可在完成后刷新账号与模型

#### Scenario: Word 面板关闭

- **WHEN** Word/WPS Writer 面板被释放
- **THEN** 正在进行的 ACP 探测或授权操作被取消并释放
- **AND THEN** 迟到的结果不得更新已关闭的面板

### Requirement: Word 面板使用文档术语

Word/WPS Writer 面板 SHALL 显示文档助手欢迎语和附件提示，并隐藏 Excel 专属的表格排版快捷操作；Excel 面板 SHALL 保持现有文案与操作。

#### Scenario: Word 首屏

- **WHEN** Word 面板加载聊天页
- **THEN** 欢迎语和示例使用“文档”术语
- **AND THEN** 不显示“工作簿”“工作表”或“适配当前表”快捷操作
