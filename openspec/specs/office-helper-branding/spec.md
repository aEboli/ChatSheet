# office-helper-branding Specification

## Purpose
让用户在 Excel、Word 及兼容的 WPS 宿主间识别同一 Office-helper 产品，同时保留既有 Excel 加载项安装的兼容标识。

## Requirements

### Requirement: Office 宿主名称一致

Excel 与 Word/WPS Writer 加载项的用户可见产品名称 SHALL 为 `Office-helper`，包括面板标题、面板顶部名称、Ribbon 产品入口和欢迎语。面向 WorkBuddy ACP 的客户端名称 SHALL 为 `Office-helper`。

#### Scenario: 在 Excel 中打开加载项

- **WHEN** 用户在 Excel 或 WPS 表格中打开加载项
- **THEN** 面板、欢迎语和 Ribbon 产品入口均 SHALL 显示 `Office-helper`

#### Scenario: 在 Word 中打开加载项

- **WHEN** 用户在 Word 或 WPS Writer 中打开加载项
- **THEN** 面板和 Ribbon 产品入口 SHALL 使用 `Office-helper` 名称

#### Scenario: 初始化 WorkBuddy 客户端

- **WHEN** Office-helper 与 WorkBuddy ACP 建立客户端会话
- **THEN** ACP 客户端名称 SHALL 为 `Office-helper`

### Requirement: Excel 加载项兼容标识保持稳定

Office-helper SHALL 保留既有 Excel ProgID、CLSID、任务窗格 ProgID 和本机数据目录，确保已有安装可以升级并继续读取本地设置。

#### Scenario: 升级既有 Excel 安装

- **WHEN** 已有用户安装显示名为 Office-helper 的新版本
- **THEN** Excel SHALL 继续通过既有 COM 标识加载入口
- **AND** 原有本机设置目录 SHALL 保持可用
