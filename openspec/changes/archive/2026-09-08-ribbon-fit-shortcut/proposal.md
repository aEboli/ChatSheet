## Why

“适配当前表”目前只能从右侧面板底部触发。用户只想整理当前工作表时，必须先打开面板再寻找按钮；而 ChatSheet 功能区已经承载“设置”和“诊断”，适合作为后续常用动作的快捷功能区。

## What Changes

- 在 Excel/WPS 的 `ChatSheet` 功能区选项卡中新增“适配当前表”按钮，排列在“设置”和“诊断”之后。
- 点击快捷按钮时打开或保持 ChatSheet 面板、切回对话页，并触发现有适配动作；操作结果、错误和撤销仍使用现有操作卡片呈现。
- 面板尚未加载完成时保留这次点击，待页面就绪后只执行一次，避免首次点击无效。
- 保留面板内现有适配入口；功能区按钮是同一能力的快捷入口，不复制适配实现。

## Capabilities

### New Capabilities

- `ribbon-shortcuts`: 规定 ChatSheet 功能区快捷入口的顺序、触发语义及冷启动行为。

### Modified Capabilities

无。

## Impact

- 功能区声明：`src/ChatSheet.AddIn/Resources/Ribbon.xml`
- 功能区回调与面板转发：`ComAddIn.cs`、`TaskPaneController.cs`、`TaskPaneControl.cs`
- 测试：新增功能区结构与回调链路的静态验证，并复跑现有适配入口及构建测试
- 不新增外部依赖，不改变 `fit_range`、撤销记录或面板操作卡片协议
