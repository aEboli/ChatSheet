# 任务

## 面板

- [x] `app.css`：加 `.notice-ok` 变体，用主色三件套（`accent-bg` / `accent-border` /
      `accent-text`），描边不用满饱和度的 `accent`
- [x] `chat.js`：`markTurnComplete`，只在 `turn-complete` 里调用
- [x] `chat.js`：标记带 `notice-complete` 类与悬停说明（讲清没有它意味着什么）
- [x] `chat.js`：`sealOpsBatch` 成组后把完成标记重新挂到末尾，同时刷新挂载序号
- [x] `chat.js`：`describeChatLayout` 加完成标记计数

## 测试

- [x] `tests/web/turn-complete-mark.test.mjs`：新增。正常收尾要有、四条异常路径
      都不能有、与操作组的先后、还原后先后仍成立、新会话清掉
- [x] 缺标记时断言要能干净报错而不是抛异常（拿不到节点就退回空节点）
- [x] `ops-group.test.mjs`：「组排在上一轮内容之后」改为不把完成标记算进上一轮末尾，
      并写明理由
- [x] 变异验证：不插标记（8 条报错）、停止时也插（3 条）、成组后不移回末尾（1 条）

## 规格与文档

- [x] `openspec/specs/panel-operation-cards/spec.md`：合入两条新要求
- [x] `docs/changes/2026-08-26-turn-operation-groups.md`：补一节
- [x] `README.md`：功能表补一行
- [x] 归档到 `openspec/changes/archive/`

## 验证结果

- 面板单测 16 个文件合计 **414 项通过、0 失败**（`turn-complete-mark.test.mjs` 26 项）
- Release 构建 0 警告 0 错误；工具单测 371 项通过（本次未碰加载项侧）
- 三个变异逐条验证新断言有效
- 真实 `index.html` 上经 CDP 量过：胶囊 55×30px、圆角 10px、末位；
  浅色字底对比度 **6.76:1**、深色 **8.15:1**（都过 WCAG AA 的 4.5:1）；
  与警示胶囊水平中线完全对齐（同为 203px，视口中线 210px，差值来自 15px 滚动条，
  是 `.notice` 共有的既有表现）；栏宽 320 / 420px × 深浅两套主题均无横向溢出
- 同一途径核过互斥：停止那一轮的胶囊序列为「已完成 → 已停止生成。」——
  前者属于上一轮，被停止的这一轮完成标记数仍为 1，没有多出来
- 量具用后即删
