# Office-helper Word/WPS Writer 扩展说明

本仓库保留 `ChatSheet.AddIn` 作为 Excel/WPS 表格兼容入口，并新增 `ChatWord.AddIn` 作为 Microsoft Word 桌面版和 WPS Writer 的独立入口。产品显示名为 Office-helper；旧 Excel `ChatSheet.AddIn` ProgID/CLSID 不变，保证升级时不丢失既有加载项。

## 架构边界

共享 WebView2 控件、模型提供器、设置、会话、审批消息和操作卡协议。Word 入口独立实现 `WordDocumentContext`、`WordTargetResolver`、`WordToolExecutor`、`WordUndoStore` 和 `WordSystemPrompt`，不会向 Word 模型暴露 Workbook、Worksheet、Range、Cell、A1 或 `excel_object`。

所有 Word/WPS Writer COM 访问都在面板创建时捕获的 UI/STA 同步上下文执行。对象模型使用后期绑定和 `en-US` LCID；每条路径释放中间 COM 引用，写入后重新读取目标。宿主不支持的成员和受保护/只读文档返回具体错误码，不显示成功或撤销入口。

## 官方对象模型依据

- [Word object model overview](https://learn.microsoft.com/en-us/office/vba/word/concepts/objects-properties-methods/word-object-model-overview)
- [Document object](https://learn.microsoft.com/en-us/office/vba/api/word.document)
- [Range object](https://learn.microsoft.com/en-us/office/vba/api/word.range)
- [StoryRanges property](https://learn.microsoft.com/en-us/office/vba/api/word.document.storyranges)
- [Selection object](https://learn.microsoft.com/en-us/office/vba/api/word.selection)
- [Find object](https://learn.microsoft.com/en-us/office/vba/api/word.find)
- [ContentControls collection](https://learn.microsoft.com/en-us/office/vba/api/word.contentcontrols)
- [Bookmarks collection](https://learn.microsoft.com/en-us/office/vba/api/word.bookmarks)
- [Fields collection](https://learn.microsoft.com/en-us/office/vba/api/word.fields)
- [Revisions collection](https://learn.microsoft.com/en-us/office/vba/api/word.revisions)
- [Document protection](https://learn.microsoft.com/en-us/office/vba/word/concepts/working-with-word/working-with-protection)

## 宿主能力探测

本机探测结果（2026-09-23）：

| 能力 | Microsoft Word 16.0 / Build 16.0.17932 | WPS Writer 12.0 / Build 12.8.2.19823 |
| --- | --- | --- |
| ProgID | `Word.Application` | `kwps.Application`、`KWPS.Application` |
| `Application.Name` | Microsoft Word | 返回 Microsoft Word，不能单独识别 WPS |
| 基本文档/段落/表格/节 | 已探测 | 已探测 |
| Selection/Range Start/End/Text | 已探测 | 已探测，非正文 Story 偏移有差异 |
| Header/Footer、Footnote/Endnote、Comments | 已探测 | 已探测部分 Story |
| ContentControls/Bookmarks/Fields/Revisions | 已探测 | 已探测，具体成员仍按版本逐次回读 |
| StoryRanges 数量 | 15 类（加入对应内容后） | 6 类（当前版本探测） |
| 表格单元格结尾 | `Chr(13)+Chr(7)` | `Chr(13)+Chr(7)` |
| Document.Undo/Redo | 存在，只有栈语义 | 存在，只有栈语义 |
| Ribbon/CTP | 需真实宿主冒烟确认 | 需真实宿主冒烟确认 |

WPS Writer 的实机启用清单是 `AddinsWL` 的 ProgID 白名单值集合，而不是 Office Addins 风格的子键。安装器同时写入当前用户的 `HKCU\Software\Kingsoft\Office\WPS\AddinsWL` 和 WPS 安装级的 `HKLM\SOFTWARE\Kingsoft\Office\WPS\AddinsWL`、`HKLM\SOFTWARE\WOW6432Node\Kingsoft\Office\WPS\AddinsWL`，并写入 `OfficeHelper.Word.AddIn` 空值；旧的同名子键会被清理。不套用 WPS 表格 ET 路径。

## Word 工具目标和错误

目标使用 `target` 对象，支持：

```json
{
  "target": {
    "story": "main_text",
    "start": 120,
    "end": 160
  }
}
```

也支持 `paragraph_index`、`table_index + row + column`、`bookmark`、`content_control`。区间、段落和表格定位要求显式 `story`；缺失、冲突或跨 Story 返回 `TARGET_REQUIRED`、`TARGET_AMBIGUOUS`、`STORY_REQUIRED` 或 `TARGET_NOT_FOUND`。读写均限制长度并返回分页偏移。保护/只读、未探到成员、读回不一致和无法快照分别返回 `PROTECTED_DOCUMENT`、`UNSUPPORTED_MEMBER`、`READBACK_FAILED`、`UNDO_UNAVAILABLE` 等具体状态。

## 验证

- `dotnet build ChatSheet.sln --configuration Release`
- `ChatSheet.ToolTests.exe --word-tests`：假的后期绑定对象、目标解析、控制字符清理、工具 Schema、系统提示和 Word/WPS 识别。
- 现有 `--reliability-tests`、Excel/WPS 表格项目审计和全部网页测试继续运行。
- 安装 Microsoft Word 或 WPS Writer 时，使用真实文档执行结构/选区读取、审批写入、读回和快照撤销；宿主不可用时测试脚本返回“未执行及原因”，不伪造成功。
