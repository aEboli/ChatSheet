# 2026-08-25：适配改用操作卡片，并标出来源

面板「适配」不再呈现为提示胶囊，改用与模型发起的操作相同的卡片；区别改由摘要行上的
「手动」标记和边条颜色承担。

## 问题

两类操作原先是两种样式：

```text
模型发起：  ▸ 写入值            已写入 2-8行 × A-D列，共 28 个单元格  [撤销]
面板适配：      ⌐ 已适配 1-6行 × A-D列：水平居中、垂直居中… [撤销] ⌐
```

要回答的问题完全一样——改了哪个范围、影响多少格、能不能撤销——却要在两处找同一种
信息。适配的影响面挤在一句话里，模型操作的在摘要行右端；适配没有参数可看，
模型操作展开就有。

而真正值得区分的那件事——**谁发起的**——原先没有被表达出来，反倒被两种不相干的
样式差异盖过去了。

## 改动

适配改用同一张卡片，区别只剩来源：

```text
▸ 适配  手动      已适配 1-6行 × A-D列，共 24 个单元格   [撤销]
▲边条换主色
```

卡片在点下按钮时就上屏并显示「执行中…」，结果到了原地填充。整表适配可能跑上
一两分钟，这段时间里摘要行本身就是进度反馈——和模型发起的工具一致。

**「手动」标记是主要手段，颜色是辅助。** 只靠边条颜色不行：颜色说不出区别在哪，
得先知道「绿色代表手动」才读得懂；色觉障碍或 Windows 高对比模式下也可能根本看不出
来。标记不论折叠还是展开都在，是这两类操作唯一始终可读的差别。悬停说明写的是
「你在面板上点按钮直接执行的，不是模型发起的」——把区别本身说出来，而不是让人猜。

已撤销与失败的样式优先于来源色，那两个状态更要紧；标记则始终保留。

**失败也走同一张卡片。** 原先宿主拒绝会另起一条错误提示，而那张卡片当时并不存在。
现在卡片已经在屏上，无论成功、被拒还是调用本身抛异常，都填回同一张——不会留下一张
永远停在「执行中…」的卡。

适配仍然不进对话历史：确定性动作，点按钮已经表达了意图，模型不必知道。卡片只给
用户看。

## 撤销标识的时序坎

记录标识要等宿主执行完才知道，而卡片在那之前就得上屏。于是卡片先用临时标识
（`fit-pending-N`）占位，结果到了再改写成宿主回传的 `undoId`。

撤销必须按后者发出去。这条路径上正是出过缺陷的地方——v0.2.1 修的就是「点撤销报
找不到该操作记录」，成因是面板拿到了一个宿主那边并不存在的标识。因此
`fit-card.test.mjs` 里专门有一条断言盯着撤销请求发出去的到底是哪个标识。

为此把 `finishToolCard(payload)` 拆成两层：按标识找卡片的那层保持原样，填充逻辑
挪进 `fillToolCard(card, payload)`。适配拿得到卡片引用但拿不到标识，正需要后者。

## 顺带清掉的

`addUndoableNotice` 与 `.notice-undo` 只有适配一个调用方，改完就没人用了，一并删除。

## 假 DOM 要做实的一处

新增的 `fit-card.test.mjs` 里 `querySelector` 是真的按类名走子树，而不是像其他面板
单测那样返回 `null`。卡片的填充全靠 `card.querySelector('.tool-state')` 这类调用，
返回 `null` 的话被测代码直接抛异常——测试会「失败」，但失败原因是假件不够而不是
被测行为不对，什么也验不到。

## 涉及文件

| 文件 | 改动 |
| --- | --- |
| `src/web/scripts/chat.js` | 来源标记；`fillToolCard`；`runFit` 改走卡片；删 `addUndoableNotice` |
| `src/web/styles/app.css` | `.tool-card.is-manual`、`.tool-origin`、`.tool-note`；删 `.notice-undo` |
| `src/ChatSheet.AddIn/AddInAutomation.cs` | `ReadLastToolCardForTest` |
| `src/ChatSheet.AddIn/ComAddIn.cs` | `ReadLastToolCardForAutomation` |
| `src/ChatSheet.AddIn/TaskPaneController.cs` | `ReadLastToolCard` |
| `src/ChatSheet.AddIn/TaskPaneControl.cs` | `ReadLastToolCard`；`ReadLastNotice` 注释 |
| `scripts/verify-fit-undo.ps1` | 读卡片替代读提示；来源与状态断言 |
| `tests/web/fit-card.test.mjs` | 新增 32 项 |

## 验证

- 面板单测 11 个文件合计 254 项通过，失败 0
- `ChatSheet.ToolTests.exe`（真实 Excel）：342 通过 / 0 失败，无回归
- Release 构建 0 警告 0 错误
- `verify-fit-undo.ps1` 已按新 DOM 改好，需要启动真实 Excel，改动后未在本机执行
