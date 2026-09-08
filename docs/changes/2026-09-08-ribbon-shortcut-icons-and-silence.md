# 2026-09-08：功能区快捷区——图标、静默适配、只撤功能区操作的撤销

ChatSheet 功能区「设置 / 诊断」后面加了一段快捷区。点「适配当前表」直接排好眼前这张表，不必打开面板；旁边的「撤销」只撤你在功能区点出来的改动。四个按钮都有独立图标和悬停说明。

## 问题

「适配」原先只能从右侧面板底部触发。只想整理当前工作表时，必须先打开面板再找按钮。GPT 把按钮接到了功能区，但：

```text
点「适配当前表」 → 面板弹出来，切到对话页
面板打不开     → 再弹一个「ChatSheet 面板打不开」
三个按钮        → 只有文字，没有图标
撤销            → 还得打开面板去找卡片
```

用户点的是「把这页排好」，不是「打开 ChatSheet」。COM 写入又会清空 Excel 自己的撤销栈，Ctrl+Z 拿不回来——快捷区如果没有自己的撤销，前一半就白省了。

## 改动

```text
点「适配当前表」  →  表被排好，面板保持原状（关着就关着）
点「撤销」        →  只撤功能区点过的；面板里点的、模型改的都不碰
没有可撤的        →  按钮是灰的
会盖掉后续改动    →  第一次不执行，按钮改叫「仍然撤销」
设置 / 诊断       →  仍打开对应的页，按钮上有了图标
```

几处：

- `Ribbon.xml`：三个打开页的按钮与快捷区之间加分隔符；适配后面跟撤销；四个按钮各绑独立 `getImage`，各有 screentip / supertip（撤销的说明是回调，能写出即将撤掉哪一步）。
- `ComAddIn.RibbonImage.cs`：按资源名缓存 IPictureDisp。每个按钮一个回调，避免按 `control.Id` 分派——读不到 Id 只能返回 null，表现与资源缺失完全相同。
- `ComAddIn.OnFitCurrentSheet`：只 `EnsurePane` + 转发，不 `ApplyPaneVisibility`。面板不存在时在后台建一个并保持隐藏——它是撤销记录的归属。
- `ComAddIn.OnUndoRibbonAction`：转发到页面入口，只点带 `data-source=ribbon` 的卡片上现有的撤销按钮。启用态、标签、悬停说明读一份异步回读写入的缓存；回调里不问页面，以免卡住功能区绘制。
- `TaskPaneControl`：冷启动排队加上界（10 秒）。导航失败时不再把这次点击留到下次页面加载时突然生效。
- `chat.js`：功能区适配给卡片打 `data-source=ribbon`；`__chatsheetRibbonUndo` 只找这些卡，已经撤过的跳过，重叠警告仍要二次确认。

图标几何只写在 `scripts/make-ribbon-icons.ps1`，与面板 Logo 同一近黑 `#111817`、同一 64×64。

## 验证

```powershell
node tests\web\ribbon-icons.test.mjs
node tests\web\ribbon-fit-shortcut.test.mjs
node tests\web\ribbon-undo.test.mjs
```

像素测试锁住「有内容、形状可辨、墨色正确、撤销箭头朝左」：把诊断图标换成全透明、把齿轮换成实心圆饼，会分别失败 4 条和 2 条。静态测试锁住顺序、独立图标回调、静默、有界重试、撤销只认功能区标记。行为测试锁住「面板点的不计入、点中的是功能区那张卡、重叠要二次确认、正忙不重复投递」。
