# 批量探测：探完一个就上色，在飞的那几行都标出来

用户报的是「批量探测不够直观，探测失败的没有变红，成功的没有标绿」。

查下来是两件独立的事，都落在同一个功能上。而 CSS 一行没动——`.picker-item.is-available`
与 `.is-unavailable` 的颜色规则自 `v0.6.0` 就在，两套主题各一份，`--accent` 与 `--error`
两个变量也都在。缺的是把行送进那两条规则的那一步。

## 一、名单批量确认从来没有边跑边上色

`ProbeFavoritesAsync`（面板列头的「确认」）每个模型只推一条 `probe-progress`，
而且推在**探测之前**，字段只有 `kind` / `model` / `index` / `total`——没有 `verdict`。

面板侧 `picker.js` 的处理器只在 `message.verdict` 存在时才调 `recordVerdictLocally`。
于是整批跑完之前，一行都不会变色；颜色要等整批结束、由回复里的 `availability` 经
`adoptFavorites` 一次性补上。名单十几个模型就是十几次往返，中途整列都是「未确认」——
看起来就是点了没反应。

整份目录那条路（`TestAllModelsAsync`）一直是每探完一个就带 `verdict` 推一次的，
这条只是漏了。修法就是把它补齐：探完（无论正常返回还是 `ProviderException` 被
`Classify` 判成不可用）都推一条带 `verdict` 的。

## 二、扫光永远落在一行已经上了色的行上

`ProbeManyAsync` 此前只有「探完一个」的回调，没有「开始探一个」的。而整份目录那条路
并发 5：同一时刻在飞的是五个模型。

面板侧用 `bulk.model` 一个字符串记「正在测哪一个」，它只能由推送来填，而推送只在探完
之后发——所以那个字段装的永远是**刚探完**的那一个。结果是扫光落在一行刚刚变绿或变红
的行上：已经有结论了却还挂着「正在测」的高光，而真正在飞的五个一个都没标。

两个互相矛盾的标记压在同一行，读不出这一行到底测完了没有。

修法分两头：

- `ModelProbe.ProbeManyAsync` 新增可选的 `onStart`，在模型**拿到并发槽位之后**、
  发请求之前回调。时机不能提前到入队时——排在后面等槽位的还没开始发请求，提前标上
  等于说假话。
- 面板把「正在探」从单个字段改成集合（`state.testing`），由推送两端驱动：
  `starting` 加进去，`settled` 摘出来。并发 5 就是 5 行同时扫。

`setBulkProgress(null)` 时集合一并清空。必须在那里清：用户中途点停止，已经在飞的那几个
不会再收到 `settled`（后端直接跳出循环），只靠逐个摘的话批量早停了、列表里还有几行在扫。

## 三、顺带修掉一处两侧不一致（未在用户报告里）

加载项侧 `ModelAvailability.Record` 遇 `Unknown` 直接 `return`——限流一类是「花了钱没拿到
答案」，不是证据，不该抹掉上一次测出来的结论。而面板侧 `recordVerdictLocally` 照写。

后果是看得见的：上次测出能用的模型这次被限流，行上的绿当场消失，而整批结束时权威快照
仍说「能用」，绿又回来。一行在批量途中掉了色又找回来，比从头到尾不变色更难读——而这正是
「不够直观」要修的东西。面板侧改成同一条规则。

## 改动位置

| 文件 | 改动 |
| --- | --- |
| `src/ChatSheet.AddIn/Bridge/AgentChannels.cs` | `ProbeFavoritesAsync` 探完推带 `verdict` + `settled` 的进度；开始那条加 `starting`；`TestAllModelsAsync` 的完成推送加 `settled`，并接上 `onStart` 推 `starting` |
| `src/ChatSheet.AddIn/Providers/ModelProbe.cs` | `ProbeManyAsync` 新增可选 `onStart`，在拿到并发槽位之后回调 |
| `src/web/scripts/model-favorites.js` | 「正在探」改为集合 `state.testing`，新增 `markBulkTesting` / `bulkTestingCount`；`isBulkTesting` 读集合；置空进度时清空集合；`recordVerdictLocally` 的 `Unknown` 不覆盖已有判定 |
| `src/web/scripts/picker.js` | `probe-progress` 处理器按 `starting` / `settled` 维护在飞集合；先落判定再设进度；`starting` 那条不带 `index`，进度计数沿用上一条；`describePicker` 报出「在飞」 |
| `src/ChatSheet.AddIn/TaskPaneControl.cs` | 自检注入脚本新增 `bulk-settled:<模型>:<判定>`，`bulk-testing` 那条带上 `starting` |

## 验证

**面板单测**（`tests/web/`，共 810 条）：

- `model-probe.test.mjs` 新增一节「批量确认边跑边上色」：起点三行都未确认、开始探那一行
  有扫光、探完当场标绿、上了色不再挂扫光、扫光跟着移到下一行、探失败变红、绿与红是不同
  的 class、判未确认不上色且仍留「试一下」、带判定的推送不打断进度计数。
- `model-test-all.test.mjs` 新增一节「并发在飞的那几行都被标出来」：三行同时在飞都被标、
  在飞个数报得出、没在飞的不跟着扫、探完的退出在飞、其余不受影响、开始探下一个不会让
  进度倒退、停止后没有任何一行还在扫。
- `model-favorites.test.mjs` 新增一节锁住 `Unknown` 的覆盖规则：不抹绿、不抹红、
  本来没判定的落 `Unknown` 仍是未确认、真结论照旧覆盖（双向）。
- `chat-motion.test.mjs` 改掉一条钉住旧实现形状的断言，并补两条：在飞的必须是集合、
  置空进度时清空集合。

**变异验证**（每处修复都故意改坏一次，确认断言真的会红）：删掉本地落判定 → 3 条红；
忽略 `settled` → 1 条红；让 `Unknown` 覆盖 → 2 条红；在飞退回只记一个 → 4 条红；
`onStart` 提到入队时 → 1 条红。

第一版的批量确认测试是**假绿**：复用了前一节已经探出结论的模型，把修复整段删掉断言照样
通过。改成在一个从未确认过任何模型的连接上跑，三行起点都是未确认。

**`ChatSheet.ToolTests`（702 条）**：`BulkTestTests.cs` 新增 `TestStartCallbackTracksInFlight`，
起真实 HTTP 服务跑真实 `ProbeManyAsync`，盯住只有它拦得住的那条性质——同时在飞的峰值
必须等于并发数而不是模型总数。这一条存在的理由：`onStart` 提前回调不报错、不变慢，
只会把标记变成噪音。

**`PaneHarness --picker`（76 条，真实 WebView2）**：走真实推送路径，读 `getComputedStyle`
的实际值——class 在、规则在，而变量名写错时浏览器会静默退回默认色，那时单测照样全绿。
实测：判不可用的行名字与状态点都是 `rgb(242, 139, 130)`、判未确认是 `rgb(230, 232, 234)`
且 class 光秃秃、红之后再推 `Unknown` 红不退、两行同时在飞各挂一个 `running` 的 `::after`
动画、探完的那行收掉而另一行仍在扫、批量置空后全收掉。

`--motion` / `--theme` 全绿。装好后 `%LOCALAPPDATA%\ChatSheet\app` 下的 web 资产与源码
逐字节相同。

## 未验证

**未做目视确认。** 开发环境读不了图片，五行同时扫是否显得吵、绿与红在实际列表里是否
一眼分得开，只有人眼能判断。参数与颜色都沿用既有调色板，没有新增视觉常量。
