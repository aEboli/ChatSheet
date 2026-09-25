# Office-helper Word/WPS Writer 文档助手

## Why

当前 ChatSheet 是面向 Microsoft Excel 与 WPS 表格的 COM 加载项。其上下文、目标解析、工具参数、审批预览、撤销快照和系统提示词都围绕 Workbook、Worksheet、Range 与 A1 地址设计，不能直接迁移到 Word 文档。

本变更为 Microsoft Word 桌面版和 WPS Writer 增加独立的文档助手入口，同时保留现有 Excel/WPS 表格加载项的行为和注册兼容性。Word 工具只允许访问当前文档的显式目标，不提供文件系统、Shell、宏或任意网络能力。

产品层计划改名为 Office-helper。该名称变更不能通过替换已经发布的 Excel COM 标识完成，因为现有 CLSID、ProgID 和宿主注册项一旦改变会使旧安装失效。因此默认方案是以 Office-helper 作为新产品和安装包名称，保留旧的 ChatSheet Excel 标识作为兼容入口；新的 Word 入口使用独立、一次生成后永久保留的标识。

## What Changes

- 新建独立的 Word COM Add-in 入口和宿主适配层，目标项目名暂定 `ChatWord.AddIn`，产品显示名为 Office-helper 文档助手。
- 复用 WebView2 面板框架、模型提供器、设置、会话、审批通道和操作卡交互协议；Word 的上下文、目标、工具、提示词、能力探测和撤销实现独立。
- 支持 Microsoft Word 桌面版；支持 WPS Writer 的能力以 ProgID/进程探测和逐成员读回为准，不把 WPS 表格或 Word 的兼容性推断给 Writer。
- 新增文档级上下文：文档名称/路径/保存状态/只读状态、宿主版本、页数/节数/段落数/表格数、选区 story 与 Start/End、段落/样式/标题层级、表格定位、页眉页脚、脚注、尾注、批注、修订、内容控件、书签和字段存在性。
- 新增 Word 只读和修改工具，使用 story、字符 Start/End、段落索引、表格行列、Bookmark 或 ContentControl 等显式目标，不使用 Excel A1 地址。
- 所有宿主调用在正确的 UI/STA 线程执行，后期绑定使用能力探测和 en-US LCID，COM 引用在每条路径释放；修改完成后读回验证。
- 写操作继续遵循现有审批卡、操作卡、撤销和错误处理约定；没有可靠快照或宿主记录时不得显示虚假撤销入口。
- Word Ribbon、诊断、错误和卡片文案改用“文档”“Word”“Writer”等术语，不复用“工作簿”“工作表”“单元格”或“适配当前表”。
- 安装器同时登记 Excel、WPS 表格、Word 和经实机确认的 WPS Writer 宿主路径；不猜测 WPS Writer 的注册表路径。

## Capabilities

### New Capabilities

- `word-document-operation-access`：文档上下文、显式目标、只读/修改工具、能力探测、审批预览、读回核验和真实撤销边界。
- `word-writer-host-registration`：独立 Word/WPS Writer COM 入口、Ribbon、双位数 COM 类注册和宿主登记。

### Modified Capabilities

- `panel-operation-cards`：卡片位置从表格地址扩展为文档目标摘要，但保持来源、审批、状态和撤销交互规则。
- `project-reliability`：增加文档实例绑定、Story/Range 生命周期、Word/WPS UI/STA 调度和文档快照边界。
- `windows-release-distribution`：产品显示名改为 Office-helper，并在不破坏旧注册的前提下安装两个宿主入口。

## Non-goals

- 不把 Workbook、Worksheet、Range、Cell、公式、图表、透视表或 A1 地址工具暴露给 Word。
- 不提供文件打开/保存、文件系统浏览、Shell、宏、VBA、外部链接执行或任意网络访问。
- 不把示例文档的具体产品名、型号、正文或地址写入业务代码、提示词或测试断言。
- 不承诺 WPS Writer 支持未经实机探测的 Word 成员；不以 Application.Name 单独识别 WPS。

## Confirmed Decisions

用户已确认采用以下方案：Office-helper 作为产品显示名，保留 ChatSheet Excel ProgID/CLSID；新增 `ChatWord.AddIn` 项目和独立 Word ProgID/CLSID；WPS Writer 仅写入实机探测到的 `WPS\\AddinsWL`；Word 修改工具继承现有审批设置，但保护/只读/跨 Story 和无法快照的操作必须明确拒绝或不承诺撤销。
