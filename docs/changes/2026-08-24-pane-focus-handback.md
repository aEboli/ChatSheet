# 2026-08-24：面板失焦后把键盘焦点交回 Excel

修复：在面板对话框里打过字后切回表格，左键单击单元格再按 Ctrl+A，全选的是
输入框里的文字而不是工作表。

改动已构建、验证并安装到本机（`%LOCALAPPDATA%\ChatSheet\app`）。

## 现象与实测

用合成鼠标键盘输入驱动，读 Excel UI 线程的焦点窗口（`GetGUIThreadInfo`）
与实际选区，确认了症状：

| 操作 | 焦点窗口 | 选区 |
| --- | --- | --- |
| 点面板输入框 | `Chrome_WidgetWin_1` | — |
| 点单元格 C6 | `Chrome_WidgetWin_1`（没变） | `$C$6`（变了） |
| 按 Ctrl+A | `Chrome_WidgetWin_1` | `$C$6`（没变） |

鼠标点击照常生效，所以选中框会跟着动；但按键全被面板吃掉。这也解释了
为什么现象看起来只跟 Ctrl+A 有关——实际上所有按键都没进网格。

## 成因

WebView2 的 Chromium 窗口属于另一个进程，被跨进程挂到面板控件的窗口树下。
用户点面板时 Win32 焦点直接落到那个窗口，面板控件（ActiveX 服务端）
**从头到尾收不到 `WM_SETFOCUS`**，于是 WinForms 的 ActiveX 层从未告诉 Excel
「窗格已 UI 激活」。Excel 因此认为焦点还在网格上，用户点单元格时它不会再调
`SetFocus`，焦点就一直卡在 Chromium 窗口里。

这一点用对照实验直接证实：在面板里临时放一个普通 WinForms 文本框，

| 先持有焦点的控件 | 点单元格后焦点 | Ctrl+A 结果 |
| --- | --- | --- |
| 普通 WinForms 文本框 | `EXCEL7` | `$1:$1048576`（正确） |
| WebView2 | `Chrome_WidgetWin_1` | 选区不变（错误） |

文本框收到了 `WM_SETFOCUS`，Excel 于是知道窗格已激活，用户点网格时它自己
把焦点交了回去。可见宿主本身的交接逻辑是好的，缺的只是那次通知。

## 已验证不可行的路线

让面板报告「页面取得焦点」，再调 WebView2 控件的 `Focus()` 补上通知。

ActiveX 宿主下 WinForms 不认为窗体处于激活状态（`ActiveControl` 为空），
`Focus()` 直接返回 `false`；而它引起的 `blur` 又会触发下一次 `focus`，
形成每秒数百次的焦点循环，表现为 **Excel 卡死**。这条路已排除。

## 实现

`src/ChatSheet.AddIn/PaneFocusGuard.cs`

在 Excel 的 UI 线程上装线程级鼠标钩子（`WH_MOUSE`）。用户在面板之外按下鼠标时，
若焦点仍在面板窗口树内，就先把焦点交给被点的窗口：

```csharp
var focus = GetFocus();
if (focus == IntPtr.Zero || !IsInPane(focus)) { return; }   // 绝大多数点击在此返回

var target = ((MOUSEHOOKSTRUCT)Marshal.PtrToStructure(...)).Hwnd;
if (target == IntPtr.Zero || IsInPane(target)) { return; }

SetFocus(target);
```

几个取舍：

- **钩子只装在本线程**。Chromium 窗口在另一个进程，面板内部的点击根本不会
  进入这个钩子，因此不需要担心它干扰面板自身的输入。
- **交给「被点的窗口」而不是固定交给网格**。这样编辑栏、工作表标签各自拿到
  本该属于它们的焦点；点击本身照常派发，宿主随后按正常路径处理。
- **不走 `SheetSelectionChange`**。那个事件在「点已经选中的同一个单元格」时
  不触发，会留下一个用户能直接踩到的缺口；它还会被工具写入触发。
- 钩子跑在每条鼠标消息上，因此先查焦点再读结构体，绝大多数点击两个整数比较
  就返回；回调整体包在 `try/catch` 里，抛异常会直接打断 Excel 的消息处理。

装载在 `OnHandleCreated`（此时才有窗口句柄，且正处于宿主 UI 线程），
`Dispose` 时先卸钩子再放其他资源。

## 验证

`scripts/verify-pane-focus.ps1`，九项全部通过：

| 项 | 结果 |
| --- | --- |
| 点单元格后焦点交回网格 | `EXCEL7` |
| Ctrl+A 全选工作表 | `$1:$1048576` |
| 点「已选中的同一单元格」也交回焦点 | `EXCEL7` |
| 该场景 Ctrl+A 仍全选 | `$1:$1048576` |
| 面板内打字时焦点留在面板 | `Chrome_WidgetWin_1` |
| 键入的字确实进了输入框 | 读回 `hellochatsheet` |
| 面板内 Ctrl+A 选中输入框全文 | `0-14` |
| 面板内 Ctrl+A 不改动工作表选区 | 选区不变 |
| 点功能区后方向键行为与基线一致 | 与基线同为「不移动」 |

两个让这套验证站得住的细节：

- **文字必须用 `KEYEVENTF_UNICODE` 送**。先前用 `keybd_event` 送裸 VK 码，
  Chromium 的输入管线不当作文本输入，输入框一个字都收不到——那会让
  「面板仍能打字」这一项在实际坏掉时也显示通过。
- **功能区那项与基线对照，不看绝对结果**。守卫会把焦点交给被点的窗口，而
  功能区平时并不接管焦点，这是本改动唯一可能偏离宿主原有行为的地方；
  只比较「方向键是否移动了选区」，避免把两次测量的起点差异误报成回归。

脚本在每次合成输入前重新把 Excel 抢到前台（`AttachThreadInput` +
`SetForegroundWindow`）。本机有聊天软件会间歇抢前台，一旦在点击瞬间被抢走，
输入就落到别处，而现象与「焦点没交回」完全一样；抢不到时脚本直接报错终止，
不给出不可信的结论。

## 附带改动

`ReadComposerTextForTest` 自动化接口，返回 `value|选中起-选中止`。
输入框是 `textarea`，用户键入的内容在 `value` 上，原有的
`ReadElementTextForTest` 读 `textContent` 恒为空，无法用来验证键盘输入。
