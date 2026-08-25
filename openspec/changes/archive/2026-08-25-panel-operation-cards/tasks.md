# 任务

## 面板

- [x] `chat.js`：`addToolCard` 支持 `manual`，加 `is-manual` 类与 `.tool-origin` 标记
      （带悬停说明）
- [x] `chat.js`：`finishToolCard` 拆出 `fillToolCard(card, payload)`，
      供拿得到卡片引用但拿不到标识的调用方使用
- [x] `chat.js`：`runFit` 改为先上屏卡片、结果原地填充；成功后把卡片标识
      改写成宿主回传的记录标识
- [x] `chat.js`：无撤销记录时用 `appendToolNote` 把原因写进卡片
- [x] `chat.js`：宿主拒绝与调用异常都填回同一张卡片，不再另起错误提示
- [x] `chat.js`：删掉失去调用方的 `addUndoableNotice`
- [x] `app.css`：`.tool-card.is-manual`（边条主色、底色略染）、
      `.tool-origin`、`.tool-note`；已撤销与失败的样式优先于来源色
- [x] `app.css`：删掉失去用处的 `.notice-undo`

## 自动化钩子与验证

- [x] `ReadLastToolCardForTest`：接口、`AddInAutomation`、`ComAddIn`、
      `TaskPaneController`、`TaskPaneControl` 五层，返回名称/来源/标记/状态/撤销入口/卡片数
- [x] `ReadLastNotice` 的注释去掉「适配的撤销按钮挂在提示上」
- [x] `verify-fit-undo.ps1`：三处读提示改为读卡片；新增来源、标记、名称三条断言；
      撤销后新增「状态改为已撤销」
- [x] `tests/web/fit-card.test.mjs`：新增。假 DOM 的 `querySelector` 做实
      （按类名走子树），否则卡片填充直接抛异常，什么也验不到

## 规格与文档

- [x] `openspec/specs/panel-operation-cards/spec.md`
- [x] `docs/changes/2026-08-25-panel-operation-cards.md`
- [x] README：说明适配与模型操作同卡片、靠「手动」标记区分

## 验证结果

- 面板单测 11 个文件合计 254 项通过，失败 0（新增 `fit-card.test.mjs` 32 项）
- 工具层与撤销：`ChatSheet.ToolTests.exe` 通过 342，失败 0（无回归）
- Release 构建 0 警告 0 错误
- `verify-fit-undo.ps1` 已按新 DOM 结构改好；需要启动真实 Excel，
  改动后未在本机执行
