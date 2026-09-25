# WPS Writer 加载项禁用清理

## 背景

WPS Writer 会把加载失败的 COM 加载项 ProgID 留在 `AddinsCL` 或 `AddinsBL`。后续安装虽然重新写入 `AddinsWL` 和 `LoadBehavior=3`，WPS 仍会在双击打开文档时跳过该加载项，表现为没有 Office-helper 功能区、侧边栏和新日志。

## 方案

- 安装 Word 加载项前，清理当前 `OfficeHelper.Word.AddIn` 及兼容版本值在 WPS 用户级和安装级 `AddinsCL`/`AddinsBL` 中的残留。
- 保留其他加载项的禁用值和键，不重置整个 WPS 配置。
- 诊断分别报告这些清单是否仍阻止 Office-helper。

## 非目标

- 不修改 `.docx` 文件关联、WPS 可执行文件或用户的默认宿主。
- 不清理其他加载项的黑名单记录。
