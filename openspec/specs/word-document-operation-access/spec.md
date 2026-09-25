# word-document-operation-access Specification

## Purpose
为 Microsoft Word 桌面版和 WPS Writer 提供受限、可审阅、可核验的文档上下文和工具调用，同时不改变 Excel/WPS 表格工具的对象模型或安全边界。

## Requirements

### Requirement: WPS Writer 加载项登记覆盖实际白名单

安装器 SHALL 将 Word 加载项 ProgID 写入当前用户的 WPS Writer `AddinsWL` 白名单值集合，并在目标 WPS 安装使用安装级白名单时同步写入 64 位和 32 位 `HKLM` 视图。登记 SHALL 使用 ProgID 值而不是同名子键；登记前 SHALL 从用户级和安装级 x86/x64 `AddinsCL`/`AddinsBL` 清单移除 `OfficeHelper.Word.AddIn` 及兼容的 `.1` 值；诊断 SHALL 分别报告白名单和禁用清单状态，卸载 SHALL 清理上述登记。

#### Scenario: 32 位 WPS Writer 使用安装级白名单

- **WHEN** WPS Writer 进程为 32 位并读取 `HKLM\SOFTWARE\WOW6432Node\Kingsoft\Office\WPS\AddinsWL`
- **THEN** 安装器 SHALL 在该键写入 `OfficeHelper.Word.AddIn` 值
- **AND THEN** 重新启动 WPS Writer 后加载项 SHALL 能进入 COM 加载阶段

#### Scenario: WPS 残留禁用项

- **WHEN** `OfficeHelper.Word.AddIn` 存在于用户级或安装级 `AddinsCL`/`AddinsBL`
- **THEN** 安装器 SHALL 只移除该 ProgID 及其 `.1` 兼容值
- **AND THEN** 其他加载项的禁用值 SHALL 保持不变

#### Scenario: 双击打开文档

- **WHEN** 用户完整退出 WPS 后通过文件关联打开 `.docx`
- **THEN** WPS SHALL 进入 `OfficeHelper.Word.AddIn` 的 COM `OnConnection`
- **AND THEN** 日志 SHALL 出现 Ribbon 加载和侧边栏创建记录

#### Scenario: 诊断阻止状态

- **WHEN** 用户运行安装器诊断
- **THEN** 输出 SHALL 分别显示用户级和 x86/x64 安装级 WPS 禁用清单是否仍包含 Office-helper

### Requirement: 宿主与文档上下文可验证

系统 SHALL 返回宿主名称、ProgID/进程识别依据、版本、Build、位数、当前文档名称、路径、保存状态、只读状态、页数、节数、段落数和表格数。路径为空或文档未保存时 SHALL 明确标记，不得伪造路径。

#### Scenario: WPS 不可由 Name 唯一识别

- **WHEN** WPS Writer 的 `Application.Name` 返回与 Word 相同的字符串
- **THEN** 系统 SHALL 使用进程名或已探测 ProgID 识别 WPS
- **AND THEN** 诊断 SHALL 同时显示 Name、Version、Build 和识别来源

#### Scenario: 文档能力成员缺失

- **WHEN** 宿主不支持某个统计、Story 或集合成员
- **THEN** 系统 SHALL 返回具体成员和宿主错误
- **AND THEN** 不得以 0 或空文本假报该能力存在

### Requirement: 选区和目标使用文档定位

系统 SHALL 支持 Story + Start/End、段落索引、表格索引/行/列、Bookmark 和 ContentControl 目标。一个请求 SHALL 只使用一种主要定位方式；目标缺失、冲突或跨 Story SHALL 在修改前拒绝。

#### Scenario: 明确的表格单元格

- **WHEN** 工具收到 story、table_index、row 和 column
- **THEN** 工具 SHALL 解析该 Story 中的表格单元格并返回规范化 Start/End
- **AND THEN** 用户可见文本 SHALL 去除单元格结尾控制字符

#### Scenario: 未指明 Story 的“这里”

- **WHEN** 用户请求修改但没有可用选区且目标未给出 Story/Bookmark/ContentControl
- **THEN** 系统 SHALL 询问或返回 `TARGET_REQUIRED`
- **AND THEN** 不得默认修改正文全文

### Requirement: Story、段落和内部标记安全读取

读取 SHALL 区分正文、页眉、页脚、脚注、尾注、批注和其他 Story；正文/段落标记、表格单元格结尾、字段代码、内容控件边界和隐藏文本 SHALL 不直接混入用户摘要。大文本 SHALL 分页或限制长度并返回继续位置。

#### Scenario: 表格单元格文本

- **WHEN** 读取单元格 Range.Text
- **THEN** 结果 SHALL 去除 `Chr(13)+Chr(7)` 结尾并保留真实文本
- **AND THEN** 目标元数据 SHALL 仍保留原始 End 以便后续编辑

#### Scenario: 字段和隐藏内容

- **WHEN** 读取包含 Field 或隐藏文字的范围
- **THEN** 默认结果 SHALL 返回显示文本和字段/隐藏标记摘要
- **AND THEN** 只有明确请求时才返回字段代码或隐藏文本

### Requirement: 只读工具使用明确范围

系统 SHALL 提供 `get_document_info`、`get_selection`、`read_document_structure`、`read_range`、`read_paragraphs`、`read_table` 和 `find_text`。`find_text` SHALL 要求明确 Story/目标范围或显式的文档范围选择，不得隐式搜索全文。

#### Scenario: 查找范围未明确

- **WHEN** `find_text` 只有搜索字符串而没有 Story 或目标范围
- **THEN** 系统 SHALL 返回 `TARGET_REQUIRED`

### Requirement: 修改工具不复用 Excel 工具

系统 SHALL 提供 Word 专用的 `replace_text`、`insert_text`、`delete_range`、`format_text`、`format_paragraph`、`apply_style`、`edit_table`、`set_page_setup` 和 `insert_page_break`。这些工具 SHALL 不接受 Workbook、Worksheet、Range、Cell、A1 或 Excel 枚举参数。

#### Scenario: Excel 参数混入 Word 工具

- **WHEN** Word 工具收到 `range` 或 `sheet` 参数而没有 Word target
- **THEN** 系统 SHALL 拒绝请求并返回目标参数错误

### Requirement: 修改前检查保护、修订和特殊对象

修改正文、段落、表格、页眉、页脚、脚注、尾注或批注 SHALL 要求明确目标并检查文档保护、目标 Story、字段、书签、内容控件和 Track Changes 状态。宿主不支持、目标被保护或需要修订确认时 SHALL 返回具体错误或审批信息。

#### Scenario: 受保护文档

- **WHEN** 目标属于受保护文档且宿主拒绝写入
- **THEN** 工具 SHALL 返回 `PROTECTED_DOCUMENT`
- **AND THEN** 操作卡 SHALL 不显示成功或撤销入口

#### Scenario: Find/Replace 范围

- **WHEN** `replace_text` 未提供目标范围
- **THEN** 工具 SHALL 在执行前拒绝请求
- **AND THEN** 不得替换整个文档

### Requirement: 审批预览描述实际文档变化

所有修改工具 SHALL 遵循现有审批策略。审批卡 SHALL 显示宿主、Story、Start/End 或其他定位、前后文本/格式摘要、表格行列或节信息，并说明截断数量。预览不得把内部控制字符展示给用户，也不得写入发送给模型的对话历史。

#### Scenario: 写操作审批卡

- **WHEN** 修改工具通过目标解析并等待审批
- **THEN** 审批卡 SHALL 显示 Word/WPS Writer 宿主和实际文档目标

### Requirement: 修改后读回验证

每次成功写入 SHALL 读取目标的实际文本、格式、数量或结构并返回。读回失败、目标移动、修订状态不确定或宿主只接受部分参数时 SHALL 返回部分/不确定结果，不得声称全部完成。

#### Scenario: 写入读回失败

- **WHEN** 宿主接受写入但无法读回目标
- **THEN** 工具 SHALL 返回不确定错误，不得返回成功状态

### Requirement: 撤销只承诺真实可行的方式

撤销记录 SHALL 绑定文档实例、Story 和目标身份。优先使用完整的前后快照；宿主真实记录若没有稳定可引用的 ID，不得作为唯一撤销依据。无法保存或恢复完整快照时 SHALL 不显示撤销按钮，并说明原因；撤销失败不得伪报成功。

#### Scenario: 没有完整快照

- **WHEN** 格式或页面设置操作无法保存完整前态
- **THEN** 操作卡 SHALL 不显示撤销按钮并说明 `UNDO_UNAVAILABLE`

### Requirement: UI/STA 和 COM 生命周期

所有 Word/WPS 宿主调用 SHALL 经过创建该宿主对象的 UI/STA 调度；每条调用路径 SHALL 释放产生的 COM 引用。面板关闭、文档切换、停止任务或异步回调过期后 SHALL 拒绝迟到操作。

#### Scenario: 面板关闭后的迟到写入

- **WHEN** 异步工具回调在面板关闭后返回
- **THEN** 系统 SHALL 取消或拒绝该调用并释放 COM 引用

### Requirement: 安全边界

Word 工具 SHALL 仅访问当前文档和显式 Story/Range 目标，不提供文件系统、Shell、宏、VBA、外部链接执行或任意网络访问。

#### Scenario: 请求执行 Shell

- **WHEN** 模型请求打开文件、执行宏或调用外部网络
- **THEN** 系统 SHALL 返回未知工具或安全边界错误

### Requirement: 顾问模式保持诚实

Word 顾问模式 SHALL 明确说明不能读取或修改文档；只有工具实际返回成功后才可声称完成。所有系统提示词 SHALL 使用 Word 文档助手身份和文档术语。

#### Scenario: 顾问模式修改请求

- **WHEN** 当前模型处于顾问模式并收到修改请求
- **THEN** 助手 SHALL 只给建议，不声称文档已经修改
