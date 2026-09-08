## Why

功能区「设置 / 诊断 / 适配当前表」三个按钮都没有图标，一眼扫过去分不出彼此。更关键的是，「适配当前表」会先弹出面板再切到对话页——用户点的是「把这页排好」，不是「打开 ChatSheet」。COM 写入又会清空 Excel 自己的撤销栈，快捷区如果没有自己的撤销，前一半就白省了。

## What Changes

- 给「设置」「诊断」「适配当前表」「撤销」各加一枚与面板按钮同风格的自定义图标，各自走独立的 `getImage` 回调，各有悬停说明。
- 「适配当前表」改为静默执行：不显示面板、不切页、不弹对话框；仍然经面板现有适配按钮走同一条操作卡片与撤销链路。
- 功能区末尾加「撤销」，只撤快捷区点出来的操作；会盖掉后续改动时第一次不执行，按钮改叫「仍然撤销」。
- 诊断与快捷区之间加分隔符，标明此后是可扩展的快捷功能区。
- 冷启动等待加上界：页面一直不就绪就放弃并记日志，不再把这次点击留到下次导航时突然生效。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `ribbon-shortcuts`: 快捷入口改为静默；四个按钮必须有图标和悬停说明；撤销只认功能区操作；冷启动等待有上限。

## Impact

- 功能区声明与图标资源：`Ribbon.xml`、四枚 PNG、`ComAddIn.RibbonImage.cs`
- 静默转发、冷启动与撤销：`ComAddIn.cs`、`TaskPaneController.cs`、`TaskPaneControl.cs`、`src/web/scripts/chat.js`
- 自动化入口：`AddInAutomation.cs` 增加 `FitCurrentSheetForTest`、`UndoRibbonActionForTest`
- 测试：快捷入口静态检查、图标像素检查、撤销作用范围行为测试
