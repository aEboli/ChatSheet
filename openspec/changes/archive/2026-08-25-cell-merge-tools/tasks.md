# 任务

## 工具层

- [x] `ToolLimits`：新增 `MaxMergeCells`（= 5,000），注明约束理由是撤销而非性能
- [x] `ToolCatalog`：新增 `merge_cells`（`range`、`sheet`、`across`、
      `horizontal_alignment`、`vertical_alignment`）与 `unmerge_cells`（`range`、`sheet`），
      风险级别均为 `Write`；说明写明合并会丢弃非锚点内容、应先读一遍范围
- [x] `Tools/ToolExecutor.Merge.cs`：`MergeCells` 与 `UnmergeCells`
- [x] 对齐值在动手前解析：非法值必须在合并生效之前被拒绝
- [x] 单格范围、`across` 为真但只有一列 → `NOTHING_TO_MERGE`
- [x] 合并前整片读值统计 `discarded_values`；读回实际合并出的区域数
- [x] 范围内无合并可拆 → `NO_MERGED_CELLS`，不留空撤销记录
- [x] 执行期间关闭 `DisplayAlerts` 并在之后还原
- [x] `ToolExecutor.Read.cs`：分派两个新工具；`DescribeForUndo` 加中文描述

## 撤销

- [x] `SnapshotDetail.Merge`；`RangeSnapshot.MergeAreas`（空列表与 null 语义不同）
- [x] `SnapshotCapture.ReadMergeAreas`：范围级 `MergeCells` 为 false 时一次断定无合并，
      否则逐格读 `MergeArea` 去重；跨界区域整块记下
- [x] `SnapshotCapture.Restore`：有合并快照时先拆平、最后按快照合回；
      单块合不回去不影响其余块
- [x] `UndoStore.DetailFor`：`merge_cells` → `Content | Format | Merge`；
      `unmerge_cells` → `Merge`
- [x] `ToolExecutor.TryCapture`：把 `Merge` 纳入「逐格」判断，使其受单元格上限约束

## 提示与面板

- [x] `SystemPrompt` 能力边界一行加入合并与取消合并
- [x] `chat.js` 工具标签：`merge_cells`、`unmerge_cells`，并补上漏掉的 `fit_range`

## 测试

- [x] `Program.cs` 新增 10 条工具用例：两类 `NOTHING_TO_MERGE`、非法对齐、
      带值合并的 `discarded_values`、拆合并、`NO_MERGED_CELLS`、逐行合并三区域、
      逐行拆开、超限拦截
- [x] `UndoTests.cs` 新增 11 条断言：合并生效、能撤销、拆回独立单元格、
      被丢弃的值已找回、对齐已回退、能恢复；取消合并能撤销、区域已装回、能恢复；
      在已有合并上再合并——撤销后原有区域装回、范围外缘的值找回

## 文档

- [x] `docs/changes/2026-08-25-cell-merge-tools.md`
- [x] README：核心能力表「格式与数据」一行、能力清单、安全边界表的单次数据量一行

## 验证结果

- 工具层与撤销：`ChatSheet.ToolTests.exe` 通过 342，失败 0（新增 21 项）
- 面板单测 10 个文件合计 222 项通过，失败 0
- Release 构建 0 警告 0 错误
- 未跑：需要启动真实 Excel 的 PowerShell 验证脚本（本次未涉及面板 DOM 与桥接改动）
