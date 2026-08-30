# 任务：判定一眼可见，档位一行一档

证据与取舍见 `proposal.md`。前两期见 `2026-08-29-model-availability`
与 `2026-08-29-model-probe`。

## 判定的可见性

- [x] `picker.js`：模型行带 `is-available` / `is-unavailable` / `is-probing`
- [x] `app.css`：`.picker-item.is-unavailable .picker-item-name` 用 `var(--error)`
- [x] 可用不上绿——`--accent` 是选中态的颜色，两处同色会分不清「能用」与「在用」
- [x] 选中态盖过不可用的红字，否则选中的行读不出是选中的
- [x] `app.css`：`.picker-model.is-unavailable` 从 `--warn-fg` 改 `--error`，
      与列表里同一个颜色
- [x] `picker.js`：三态的结论文字进行的 `title`，行上不再多占一行
- [x] 「正在确认」仍留在行上——那一态没有颜色可依，且它是「慢网关 / 点了没反应」
      的唯一分辨依据
- [x] `verdictHint` 的未确认从空串改成「还没确认过」：它现在只进悬停，
      空串会让那一态的悬停说明缺一句
- [x] `app.css`：正在确认的点加 `pending-pulse`；减少动效时显式恢复 `opacity: 1`
      （否则停在动画起始的低透明度上，几乎看不见）

## 浮层竖排两段

- [x] `index.html`：模型段加 `picker-col-models`，两段改上下排布
- [x] `app.css`：`.picker-pop` 改 `flex-direction: column`，`min-width` 260→300
- [x] `app.css`：`.picker-col-thinking` 的分隔线从 `border-left` 改 `border-top`
- [x] `app.css`：`.picker-col-head` 加 `flex: 0 0 auto`，模型段滚动时列头不跟着滚走
- [x] 浮层限总高 `min(60vh, calc(100vh - 132px))`。只靠减法不行：输入框会长到 200px，
      那时可用高度更小而常数是固定的
- [x] `min-width` 写成 `min(300px, calc(100vw - 24px))`：CSS 里 `min-width` 永远赢过
      `max-width`，写死 300px 时窄面板下浮层右侧被静默裁掉
- [x] 压缩次序：模型列表 `min-height: 80px`（约三行）先让，档位列表可滚且
      `min-height: 0` 后让。两段都不会被压到看不见

## 档位一行一档

- [x] `picker.js`：`buildThinkingRow` 取代共用的 `buildRow`（后者已只剩一个调用点）
- [x] `app.css`：`.picker-item-line` 横排、名字在左标注在右、不折行
- [x] `app.css`：`.picker-thinking-tag` 用调色板的琥珀三色
- [x] 用途说明进 `title`；降级标注留在行上——它不是解释，是「选了不生效」
- [x] `index.html`：档位列头加「悬停看说明」，否则说明去哪了没有任何线索

## 「试一下」悬停才浮出

- [x] `app.css`：绝对定位、`opacity: 0`、`pointer-events: none`
- [x] `.picker-row` 加 `position: relative` 作定位基准
- [x] `:hover` 与 `:focus-within` 都显形——只给 hover 时键盘用户拿不到
- [x] `@media (hover: none)` 下常显：触屏没有「停上去」这个动作
- [x] 不用 `display: none`：浮出时行宽会变，长模型名当场重新折行

## 端到端钩子

- [x] `TaskPaneControl.DrivePicker` 的 `verdict:` 改报字段形式
      （`状态=` / `标记=` / `行内=` / `悬停=` / `有试一下=`）
- [x] 状态用 `状态=<值>` 报，不裸报值：「可用」是「不可用」的子串，
      裸报时「判为可用」这条断言会在其实不可用时通过
- [x] 新增 `probe-visible:`：报算出来的 `opacity` 与 `pointer-events`。
      断言 class 在不在证明不了藏没藏
- [x] 新增 `thinking-row:`：报行内说明、降级标注、悬停说明、行高、行宽
- [x] 新增 `name-color:`：报模型名与状态点算出来的颜色
- [x] 新增 `pop-geometry`：报浮层四条边、两段高度、是否出界（含右出界）
- [x] 新增 `seed-demo` / `seed-state`：注入三态齐全的模型列表。
      动态 import 面板自己的模块——同 URL 重复 import 返回同一个实例，
      因此调到的是页面正在用的那一个
- [x] 新增 `manual:`：派发真实 submit，与用户按 Enter 同一路径
- [x] `AddInAutomation` 的 `DrivePickerForTest` 注释补上新动作

## 真实渲染器里的验证

- [x] `PaneHarness --picker`：注入三态、展开浮层，量颜色 / 行高 / 行宽 /
      `opacity` / 浮层四边，两套主题各跑一遍
- [x] `--width` / `--height` 可指定：`min-width` 与 `max-height` 只在极端尺寸下
      暴露问题，常见宽度下量出来永远是「没出界」
- [x] 红色只断言「是红的」（R 比 G、B 高出一截），不断言色号——
      调色板微调一次就会让断言失败，而要守的是「它是红的」
- [x] 起始主题读出来再报，不写死「先浅后深」：起始态取决于上次存了什么
- [x] 主题按钮在浮层外，点它会触发「点外部即关闭」，切主题后要重新展开再量

## 测试

- [x] `tests/web/picker-legibility.test.mjs`：判定 class、结论进 title、
      正在确认仍在行上、档位一行一档、降级标注不收起，外加对 `app.css` 的静态核对
- [x] 变异四次确认断言会红：去掉 `is-unavailable`、把结论文字放回行上、
      去掉 `opacity: 0`、去掉 `:focus-within`
- [x] 假 DOM 末尾常驻「清空两列后断言必须转红」的自检
- [x] `verify-picker.ps1`：状态按字段全等比对；新增确认前不带结论标记、
      结论在行上有标记、说明在悬停里、「试一下」不悬停时透明度为 0、
      档位一行一档且拿到整段宽度

## 文档

- [x] README「找出哪几个模型真能用」：三态的表述改成「主要看模型名的颜色」，
      并说明可用为何不上绿
- [x] README 思考档位那一节：补上竖排两段、一行一档、说明进悬停、降级标注不收起
- [x] `docs/changes/2026-08-30-picker-legibility.md`
- [x] 归档三个变更目录（前两期与本期），三份增量合成 openspec/specs/model-availability/spec.md 共 23 条要求

## 验证结果

- 构建 Debug 与 Release 均 0 错误 0 警告。
- C# 整套 524 条通过（与改动前同数，本次未加 C# 单测——改动都在面板与钩子上）。
- `tests/web` 21 个文件 549 条断言全通过，其中新增
  `picker-legibility.test.mjs` 41 条。
- `PaneHarness --theme` 通过；`--picker` 在 7 种窗口尺寸下全部通过：
  420x760、320x760、380x520、420x400、420x340、300x320、560x900。
- 两套主题实测：不可用的模型名浅色 `rgb(180,35,24)`、深色 `rgb(242,139,130)`，
  各自取到本套调色板那一份红；档位行高 26px、宽 292px（浮层 300px）；
  「试一下」不悬停时 `opacity: 0` 且 `pointer-events: none`。
- 矮面板下的压缩次序实测：视口 361px 时模型段守住 80px 并可滚、档位段让到 100px；
  视口 301px 时档位段让到 72px，浮层顶边 89px 未出界。

### 变异验证

1. **真实渲染器当场抓到一个静态检查与假 DOM 都放过的缺陷**：`min-width: 300px`
   在 304px 的面板上让浮层比面板宽 12px，右侧被静默裁掉（`右出界=true`）。
   CSS 里 `min-width` 永远赢过 `max-width`，而 body 不产生横向滚动条，
   那部分内容没有任何途径看到。改成 `min(300px, calc(100vw - 24px))` 后
   320/380/420 三种宽度全部通过；把它改回写死值，320px 那一档立即转红。
2. 面板单测四处变异逐一转红（见上）。
3. **一次差点造成假绿的操作**：把 `app.css` 还原后 `PaneHarness` 仍然报红——
   还原时文件时间戳早于已部署的产物，MSBuild 的增量复制跳过了它，
   跑的还是变异版。核对 `bin/Release/web/styles/app.css` 才看出来。
   本仓的「面板缺陷先查装的是哪个构建」正是这一条。
