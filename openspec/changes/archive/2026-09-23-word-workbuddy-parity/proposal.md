# Word 面板 WorkBuddy 与模型目录对齐

## Why

Word/WPS Writer 复用 Excel 的 WebView2 面板，但 Word 桥接层仍把模型目录固定为空，且没有接入 WorkBuddy ACP 的授权、登录、刷新和取消通道。结果是 Word 设置页看得到控件，却无法获取模型或完成授权；首屏和附件提示也会继续显示表格术语。

## What Changes

- 让 Word 桥接复用已有的 WorkBuddy ACP、模型目录、代理测试和模型探测通道。
- 保留 Word 自己的对话、审批、上下文、撤销和文档操作处理，避免把 Excel 工具通道注入 Word。
- 让共享面板根据 Word/WPS 宿主显示文档助手欢迎语与附件提示，并继续隐藏表格专属快捷操作。
- 在 Word 桥生命周期结束时取消并释放共享的授权/模型通道。

## Non-goals

- 不复制或读取 WorkBuddy 凭据。
- 不改变 Excel 面板或 Excel 工具协议。
- 不把 Excel 工作簿工具暴露给 Word。
