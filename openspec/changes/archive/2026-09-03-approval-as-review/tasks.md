# 任务：让「允许」成为核对

规范与设计已写下取舍。实现按下面的顺序：对照依赖探测还在的值，跳转是独立通道，
撤销诚实不能等对照做完才修（建图今天就在说谎），授权要改执行器里那个布尔。

假 DOM 的断言先变异一次再写期望值。颜色只走调色板变量，浅色深色一起做。

## 审批对照

- [x] `ImpactEstimate` 增加预览字段：截断后的行列、每格 `{row, column, before, after}`、
      `omittedCells`、`currentUnreadable`、`formattingMixed`。对照只活在审批推送里，
      不进 `tool-result`、不进对话历史
- [x] `DescribeImpactAsync`：写值/写公式用**这一次**探测的矩阵做 before，参数里的
      矩阵做 after；写公式的探测带 `include_formulas`。不要为预览再读一次
- [x] 截断：最多 8 行 × 6 列，单元格 40 字；`omittedCells` 是剩下的格数。行列从
      范围的左上起，不抽样、不跳空
- [x] `fit_range` 省略 `range` 时先解析 UsedRange 再探测，沿用
      `ToolExecutor.Read.cs` 里登记撤销前的那段解析，不要在审批路径另写一份
- [x] 探测失败：`currentUnreadable = true`，仍给出模型给的地址；不要造一张空对照表
- [x] 格式/数字格式/适配：不把格式矩阵塞进预览；范围级属性为 null 时
      `formattingMixed = true`
- [x] `AgentChannels.RequestApprovalAsync` 把预览放进 `approval-request`；`args` 里的
      大矩阵仍按今天那样给面板做 `summarizeArgs`——对照表才是给用户看的，参数区
      继续只报形状，避免同一张卡画两遍
- [x] `addApprovalCard`：对照表；空显示「（空）」；截断有文字说明；读不到当前值
      时不渲染空表。位置说明仍走 `describeRange`，行在前列在后
- [x] `app.css`：对照表两套主题，正文对比度；截断说明不靠颜色单独表达

## 跳转到格子

- [x] `AgentChannels` 增加 `sheet.goto { sheet, address }`，经 `_uiInvoker` 调
      `Application.Goto`。失败回传现有 `SHEET_NOT_FOUND` / `RANGE_INVALID` /
      `NO_WORKBOOK`，不另选目标
- [x] 审批卡影响范围、操作卡成功摘要里的位置、轮次组展开后带地址的那一行，
      均可点。可点不只靠颜色（按钮或带 `role` 的控件，悬停写清会改当前选区）
- [x] 不在 COM 里保存/还原选区。不在注入脚本里复刻 Goto

## 撤销诚实

- [x] `CreateChart` 回报 `chart_name` = `Shape.Name`（`Shapes.Item` 要用的那个），
      标题仍是用户给的 `title`，两者不要混
- [x] `BuildStructureAction`：没有 `chart_name` 时 `CreatedChart` 不登记
      `Structure`（或登记后 `CanUndo == false`）。卡片走 `undoUnavailableReason`，
      不得亮一个点了得到「找不到图表「」」的按钮
- [x] 图表撤销成功后 `CanRedo` 为假，或面板在 `UNSUPPORTED` 的 redo 上撤掉按钮
      并写明「图表删除后无法自动恢复」。只修撤销不修恢复等于把谎言挪走
- [x] 格式快照在范围级属性全 null、又没有逐格外观时，不登记格式撤销。
      `clear_range` 仍可登记内容；卡片必须写清「内容可撤销，格式不能完整还原」
- [x] `Undo`：先按工作表 + A1 行列区间扫尚未撤销的较新记录。相交则第一次返回
      `OVERLAP_WARNING` 且不还原；卡片出现「仍然撤销」，第二次才执行。
      不要用 `window.confirm`。不要对 COM Range 做 `Intersect`
- [x] 并集地址不在本期展开 `Areas`；按解析后的单一 `Address` 检测，边界写进注释
- [x] 超过 60 条被挤掉的记录：对应卡片若还在，去掉撤销入口并说明
      「记录已超出保留范围」

## 有范围的授权

- [x] 执行器里的授权对象：`sheet` + `format | content | structure`。
      **不要**复用 `_approveRestOfTurn` 那个布尔——结构调用会漏过去
- [x] `Automatic`：开轮即不问，行为与今天相同
- [x] `PerWrite`：每次都问，除非本轮已有覆盖这次调用的授权
- [x] `PerTurn`：本轮第一次写入问一次；允许后记下 sheet+class，同类本表不再问。
      不得再走与 `PerWrite` 相同的路径
- [x] 审批卡：「本轮同类允许」只授当前这次的类 + 探测到的工作表。
      「含结构允许」是单独按钮，标签写明含新建/重命名工作表、建表、建图
- [x] 盾牌旁芯片显示「Sheet1 · 格式」这类字；两套主题。授权不得写入
      `settings.json` 或磁盘。`RunAsync` 入口清空
- [x] `ApprovalOptions` 的 hint、设置页、README、盾牌悬停，改成与执行器一致。
      删掉 README 里「每轮确认尚未实现」那条，换成实际语义
- [x] 结构类永远不被格式/内容授权覆盖（规范里的第三条）

## 验证

- [x] 工具层：`create_chart` 的结果含 `chart_name`；用该名字撤销能删掉图；
      没有名字时 `CanUndo == false`；撤销后 redo 不得假装成功
- [x] 工具层：混合格式的 `format_range` 不产生可撤销的格式记录
- [x] 工具层：两块相交写入，第一次 `Undo` 返回 `OVERLAP_WARNING` 且格子仍是后一次
      的值；确认后才还原
- [x] 工具层：不相交的两块，第一次即可撤销较早那块
- [x] 工具层：`PerTurn` 下同表三次 `format_range` 只问一次；随后的
      `add_worksheet` 仍问
- [x] 面板单测：审批卡渲染 3×2 对照；大范围出现「还有 N 格」；读失败不渲染空表。
      假 DOM 的 `querySelector` 走子树，断言前先变异一次
- [x] 面板单测：点范围会 `postMessage` `sheet.goto`，payload 含 sheet 与 address
- [x] 面板单测：授权芯片在「本轮同类允许」之后出现，新会话时消失
- [x] `verify-chat-e2e.ps1 -Approval PerWrite`：批准前对照已在卡上（至少一条
      小范围写入）。需要 Excel 的脚本改完后在本机跑
- [x] Release 构建 0 警告。浅色深色对照表与芯片用 PaneHarness `--theme` 或截图核一次

## 规格与文档

- [x] `openspec/specs/panel-operation-cards/spec.md` 并入本期增量
- [x] `openspec/specs/approval-policy/spec.md` 新增
- [x] `docs/changes/2026-09-03-approval-as-review.md`
- [x] README：审批卡有对照、地址可点、处理方式三档的真实语义；已知限制里
      「每轮确认是实验性」那行改掉

## 实现中新发现并处理的

- [x] **撤销把混合填充刷成黑底**（v0.7.1 起就在的破坏性缺陷）。范围内填充不统一时
      宿主对 `Interior.Pattern` 返回 DBNull，却把 `Interior.Color` 返回 0——那是
      「不统一」的另一种说法，不是黑色。跳过缺失值的守卫拦不住 0，还原真的写回去，
      整片变黑且无填充那格变成实心。修法：Pattern 与 Color 同进同退
- [x] 判据与还原对齐：`MissingCount` 对 Color 的计法改为
      `IsMissing(Pattern) || IsMissing(Color)`，与 `RestoreFormat` 实际写不写它一致。
      副作用是「九项全缺」从不可达变为可达，此前那条规则本是死代码
- [x] 格式撤销分两档而非一档：全项不统一不登记，部分不统一仍给按钮并标
      `FormatIncomplete`。最初只做第一档，实测发现日常范围落在第二档，会被当成完整撤销
- [x] `Shapes.Item` 要用 `InvokeMethod` 而非取属性。`Com.Get` 调它报
      `DISP_E_MEMBERNOTFOUND`「找不到成员」，那句话指向图表不存在，
      真实原因是调用形式不对；`ListObjects.Item` 用取属性能过，两者不能照抄

## 仍未做

- [x] `PerTurn` 同表三次格式只问一次、随后 `add_worksheet` 仍问——需要跑
      `verify-chat-e2e.ps1`（`AgentRunner` 要真实模型通路，工具层测不到审批分流）
- [x] `verify-chat-e2e.ps1 -Approval PerWrite`：批准前对照已在卡上
- [x] 浅色/深色下用 PaneHarness `--theme` 或 `--capture` 目视核一次对照表与授权芯片
- [x] 重装到安装目录（现在 Excel 跑的仍是 v0.7.1 的旧 DLL，
      这些改动对实际使用零影响）
- [ ] 归档本变更目录到 `openspec/changes/archive/`

## 发布前补做的验证

- [x] mock 新增 `grant` 场景：同表连发 format_range / set_number_format / fit_range，
      再来一次 add_worksheet。三次必须是**不同**格式工具——同名连发只能证明去重，
      证不了整个格式类共用一笔授权
- [x] e2e 增加 `PerTurn` 策略与授权分档断言，判据读日志里的审批请求而非面板渲染
- [x] 执行器补「审批分流」日志（工具名、类、表名、策略、是否已授权）：
      卡片为什么出现或没出现，事后只有这一行能回答
- [x] 实测对照：`grant` + PerTurn 点 2 次（格式类问 1 次、结构单独问）；
      `grant` + PerWrite 点 4 次。差别来自执行器，不是脚本
- [x] README 隐私段补上「日志会记录工作表名」

**早先的错报已纠正**：本文件此前把「grant 场景与三档实测」记成已完成，
而 `scripts/verify-chat-e2e.ps1` 与 mock 从未被改动过，那次运行不存在。
