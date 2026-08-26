# 任务

## 面板：成组与还原

- [x] `chat.js`：新增 `mountToTranscript`，所有上屏节点经它挂载并记挂载序号
      （气泡、工具卡片、思考、提示胶囊、审批卡片、欢迎语、进展指示器）
- [x] `chat.js`：`turnOps` 收集本批操作，记 `{ card, name, risk }`
- [x] `chat.js`：`sealOpsGroup` 在下一轮开始时把上一批收成 `<details class="ops-group">`，
      落在该轮内容之后
- [x] `chat.js`：`renderOpsSummary` 给出统计（几个操作、几改几读、失败、已撤销），
      组内有失败时标 `is-error`
- [x] `chat.js`：还原按钮按挂载序号重排对话流，解散该组
- [x] `chat.js`：撤销/恢复后刷新所在组的摘要
- [x] `chat.js`：`addToolCard` 接受 `risk`；`runFit` 传 `risk: 'Write'`
- [x] `chat.js`：新会话清空 `turnOps`、`opsGroups`、轮次号与挂载序号
- [x] `chat.js`：`describeChatLayout` 加组数与组内卡片数

## 面板：样式与诊断入口

- [x] `app.css`：`.ops-group` 一套样式，只用调色板变量（两套主题）
- [x] `settings.js`：「排查」区加「打开诊断」按钮，改写那段只提功能区的说明
- [x] `index.html`：导航栏注释里「入口保留在哪」改为设置页 + 功能区
- [x] `app.js`：`describeLayout` 加操作组数

## 自动化钩子

- [x] `TaskPaneControl.ReadOperationGroups`：组数、每组摘要与卡片数、展开状态
- [x] `TaskPaneController` / `ComAddIn` / `IAddInAutomation` 三层转发
- [x] `verify-fit-undo.ps1`：适配卡片仍在组外时可撤销的断言不变，补一条读取分组状态

## 测试

- [x] `tests/web/ops-group.test.mjs`：新增。假 DOM 的 `append` 要真的从原父节点摘走
- [x] 覆盖：当前轮不成组、下一轮开始才成组、组落在该轮之后、统计文案、
      失败标记、撤销后摘要跟着变、还原回原位、还原后不再成组、手动操作归入同组
- [x] 变异验证新断言有效

## 规格与文档

- [x] `openspec/specs/panel-operation-cards/spec.md`：合入三条新要求
- [x] `docs/changes/2026-08-26-turn-operation-groups.md`
- [x] `README.md`：面板行为说明补一句
- [x] 归档到 `openspec/changes/archive/`

## 验证结果

- 面板单测 15 个文件合计 **388 项通过、0 失败**（`ops-group.test.mjs` 41 项；
  改动前 14 个文件 347 项）
- 工具单测 **371 项通过、0 失败**；Release 构建 0 警告 0 错误
- 七个变异逐条验证新断言有效（明细见改动记录）
- 真实 `index.html` 上经 CDP 量过布局：收起的组 33px vs 平铺 161px，
  栏宽 320/360/420px × 深浅两套主题均无横向溢出；诊断入口点击后确实到诊断页，
  两套主题配色都跟着走
- 量具用后即删
- 未跑：`verify-fit-undo.ps1` 要启动真实 Excel，本次未在本机执行；
  其中新增的分组状态断言是按新钩子写好的
