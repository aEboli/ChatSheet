# 任务

## 面板

- [x] `index.html` 在状态与用量之间加入排队条容器 `#queue-strip`（`role="list"`）
- [x] `app.css`：排队条 `column-reverse` 向上叠、居中靠左；状态与用量底边对齐、
      窄栏下优先压缩排队条；单条排队项样式与截断
- [x] 删掉不再使用的 `.msg-queued`、`.msg-queue-tag/label/cancel` 样式，
      「已取消」保留并改用 `.msg-cancelled-tag`
- [x] `chat.js`：新增 `renderQueueStrip` 整条重画；入队不再上屏成气泡
- [x] `chat.js`：`pumpQueue` 在轮到某条时才 `mountEntryBubble`，随后重画排队条
- [x] `chat.js`：取消与停止把被取消的条目落进对话流并标明未发送
      （`mountCancelledBubble`）；新会话 `clearQueue(false)` 不留痕
- [x] `chat.js`：布局日志改记「排队条 N 个」，仍与内部队列长度交叉对账

## 自动化钩子与验证

- [x] `TaskPaneControl.ReadQueueState`：排队项读 `.queue-chip`，位次读
      `.queue-chip-pos`，新增「排队条可见」字段
- [x] `TaskPaneControl.CancelQueued`：点 `.queue-chip-cancel`
- [x] `AddInAutomation` 接口注释更新返回值样例
- [x] `verify-chat-queue.ps1`：位次断言改为连续编号、新增排队条可见性与
      「排队中的两条尚未进对话流」、布局日志正则改为「排队条」
- [x] `send-stop.test.mjs`：假 DOM 补实 `remove()` 与 `className`/`classList`
      同源（否则整条重画的排队条断言测不出东西）；断言排队内容在排队条上、
      不进对话流、停止后收起
- [x] `queue.test.mjs`：同样补实假 DOM；断言排队条顺序与连续位次、
      只有已开跑的那条在对话流、排空后收起并三条都已挪进对话流

## 规格与文档

- [x] `openspec/specs/chat-input-queue/spec.md`：改写「排队内容可见且可单独取消」
      这条要求，明确位置在输入区旁而非对话流，并补一条开跑时挪进对话流的场景
- [x] `docs/changes/2026-08-25-queue-strip-beside-usage.md`
- [x] README 能力表中「输入排队」一行的描述

## 验证结果

- 面板单测 10 个文件：合计 214 项通过（新增 5 项）
- Release 构建 0 警告 0 错误
- 未跑：`verify-chat-queue.ps1` 等 PowerShell 脚本要启动真实 Excel，
  本次未在本机执行；脚本本身已按新 DOM 结构改好
