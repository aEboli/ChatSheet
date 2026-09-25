# Office-helper 品牌名称统一

## Why

Excel 面板与功能区仍显示 ChatSheet，而 Word/WPS Writer 已显示 Office-helper，用户在不同 Office 宿主之间切换时会看到两个产品名称。

## What Changes

- 将 Excel 与 Word/WPS Writer 面板的标题、顶部名称、Ribbon 入口和欢迎语统一显示为 Office-helper。
- 保留既有 ChatSheet Excel ProgID、CLSID、任务窗格 ProgID 和本机数据目录，保证旧安装兼容。
- 对外 ACP 客户端名称使用 Office-helper。

## Non-goals

- 不重命名程序集、命名空间、安装数据目录或已发布的 COM 标识。
- 不改写历史发行说明中对旧界面的描述。

## Capabilities

### New Capabilities

- `office-helper-branding`：Excel 与 Word/WPS Office 宿主中的统一产品显示名称与兼容标识。
