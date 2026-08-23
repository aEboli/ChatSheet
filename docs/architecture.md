# 实现要点与踩过的坑

记录实现中遇到的非显然问题。这些问题的共同特征是**报错与真实原因毫无关联**，靠猜会绕很久，因此把结论连同定位方法一并留下。

## 为什么不用 VSTO 或 Office.js

| 方案 | 为什么不选 |
| --- | --- |
| Office.js（Web 加载项） | 强制 HTTPS，必须安装开发证书、旁加载清单、常驻本地服务。与「装完即用」冲突。 |
| VSTO | 需要 VSTO 运行时与 Visual Studio 的 Office 开发组件，且完全不支持 WPS。 |
| 纯 WinForms 原生窗格 | 做不出流式 Markdown、diff 预览这类交互。 |
| **纯 COM 加载项 + WebView2** | 只依赖 Windows 自带的 .NET Framework 4.8 与 WebView2 运行时，两者在 Win11 均已预装。 |

侧边栏用 WebView2 的虚拟主机映射（`SetVirtualHostNameToFolderMapping`）直接加载本地静态文件，因此不起 HTTP 服务、不占端口、不需要证书。

## 四个陷阱

### 1. 接口声明的 MarshalAs 必须与官方 PIA 完全一致

`IDTExtensibility2.OnConnection` 的参数标注不能省：

```csharp
void OnConnection(
    [In, MarshalAs(UnmanagedType.IDispatch)] object Application,
    [In] ext_ConnectMode ConnectMode,
    [In, MarshalAs(UnmanagedType.IDispatch)] object AddInInst,
    [In, MarshalAs(UnmanagedType.SafeArray)] ref Array custom);
```

`custom` 实际是 `SAFEARRAY(VARIANT)`，缺少 `SafeArray` 标注时参数封送失败。**症状是对象能构造成功，但 `OnConnection` 的方法体永远进不去**，Excel 随后弹出「加载项出现问题，是否禁用」并把 `LoadBehavior` 改成 2。

也不要标注 `InterfaceType`：这些接口是 dual（`TypeLibType` 含 `FDual`），默认值正是 `InterfaceIsDual`；误标成 `InterfaceIsIDispatch` 会导致 vtable 中没有方法槽位。

定位方法是反射 GAC 里的官方 PIA 导出权威签名逐项对比：

```
C:\Windows\assembly\GAC\Extensibility\7.0.3300.0__b03f5f7f11d50a3a\extensibility.dll
C:\Windows\assembly\GAC_MSIL\office\15.0.0.0__71e9bce111e9429c\OFFICE.DLL
```

注意必须打印**参数特性**才看得到 `MarshalAs`，只看方法签名会漏掉。

### 2. 后期绑定必须传 en-US 区域

```csharp
private static readonly CultureInfo ComCulture = CultureInfo.GetCultureInfo("en-US");
target.GetType().InvokeMember(name, flags, null, target, args, ComCulture);
```

Office 的 IDispatch 按 LCID 解析成员，只接受 1033。`CultureInfo.InvariantCulture` 的 LCID 是 `0x7F`，会被拒绝并抛出 `0x80028018 TYPE_E_INVDATAREAD`「格式太旧或是类型库无效」，或各种 `0x800A03EC` 通用失败。

这个报错字面上完全指向别处，极易误判为类型库损坏或权限问题。修正后 14 个原本失败的工具测试全部通过。

### 3. 托管 COM 类不能注册到 HKCU

`mscoree` 不读 HKCU 下的类注册信息，HKCU 注册时激活报 `0x80070002`。用零依赖的最小探针做 A/B 对比可以证实：同样的键值结构写 HKCU 失败、写 HKLM 成功。

另外两点容易写错：

- `InprocServer32` 下的版本子键名是**程序集版本**（如 `0.1.0.0`），不是运行时版本 `v4.0.30319`。写错同样报 `0x80070002`。
- x86 与 x64 注册表视图互不可见，需同时写 `HKLM\SOFTWARE\Classes` 与 `HKLM\SOFTWARE\Classes\Wow6432Node`。

权威结构可用 `RegAsm.exe <dll> /regfile:out.reg` 生成后比对。

### 4. Resiliency 禁用黑名单会让修复后的加载项依然不加载

加载失败一次后，Excel 会把加载项写进：

```
HKCU\Software\Microsoft\Office\16.0\Excel\Resiliency\DisabledItems
```

此后**连对象都不再创建、`LoadBehavior` 也不再变化**，从外部看就像「压根没注册」。安装脚本必须自动清理该键下 value 内容匹配本加载项的项（值是 UTF-16 字节数组）。

## 功能区回调需要 IReflect

类必须用 `ClassInterfaceType.None`——改成 `AutoDual` 会与 `IDTExtensibility2` 的 DispId 冲突导致加载失败。但 `None` 下没有类接口可供按名解析，功能区的 `onAction`/`getPressed`/`onLoad` 回调会全部静默失效。

解法是实现 `IReflect` 自行接管分派（见 `ComAddIn.Dispatch.cs`）。同时要把未知成员记入日志：功能区 XML 里的回调名写错时，否则只表现为「按钮点了没反应」。

## 面板宽度与 DPI

宿主的 `CustomTaskPane.Width` 单位随显示缩放变化，不是 CSS 像素。实测在 150% 缩放下 `Width=401` 只换来 257 CSS 像素的视口，比例即缩放系数。

因此不在代码里假设 DPI，改由面板自校准：面板测量自身 CSS 宽度，把「当前值与目标值」交给加载项，加载项用 `当前宿主宽度 ÷ 当前 CSS 宽度` 现算比例后调整。这样任意缩放都正确。

另外宽度只能在窗格**可见后**设置，不可见时宿主会忽略赋值。

## 线程模型：最隐蔽的一类故障

Agent 循环里的 `await`（HTTP 流式读取等）会把执行切到线程池线程，而**两样东西都只能从 UI 线程访问**：

- WebView2 的托管包装有显式线程检查，违规抛 `CoreWebView2 members can only be accessed from the UI thread`
- 宿主 COM 对象是 STA 绑定的，跨单元调用会不稳定，可能抛 `RPC_E_SERVERCALL_RETRYLATER` 或在宿主繁忙时死锁

**症状极具欺骗性**：模型正常响应、流式解析正常、工具参数拼接正常，但每条推送都失败，界面完全没有反应，只在日志里留下一片重复告警。若推送异常又被吞掉，看起来就像「模型没回复」。更糟的是 `chat.send` 的响应也发不出去，前端的 Promise 永不 resolve，界面会永久卡在「处理中」。

处理方式是在**唯一出口**统一切换，而不是逐个修散落的 `await`——后者每新增一条推送路径就会重新引入同一问题：

- `HostBridge` 在构造时捕获 UI 线程的 `SynchronizationContext`（构造发生在 WebView2 初始化回调中，此时正处于 UI 线程），所有 `Post` 经它切回
- 一切触碰工作簿的操作经 `InvokeOnUiAsync` 切回，包括工具执行与审批前的影响估算
- 自动化接口由调用方线程进入，`TaskPaneControl` 用自身的 `InvokeRequired`/`Invoke` 编组

注意 `CustomTaskPane` 等真正的 COM 对象会被自动编组回 STA，所以 `ShowPane`、`SetPaneWidth` 从任意线程调用都正常，唯独 WebView2 会拦。这个差异会让人误判问题范围。

**工具层的单元测试跑在 `[STAThread]` 的 `Main` 上，天然是 STA，因此完全掩盖了这类问题。** 只有端到端验证才暴露得出来，这正是 `verify-chat-e2e.ps1` 存在的理由。

## 诊断手段

宿主内无法附加调试器，因此内置了三条独立通道：

| 通道 | 位置 | 用途 |
| --- | --- | --- |
| 文件日志 | `%LOCALAPPDATA%\ChatSheet\logs` | 主要诊断途径，UTF-8 带 BOM（否则记事本与 PowerShell 按 ANSI 解读会显示乱码） |
| 注册表信标 | `HKCU\Software\ChatSheet\Diagnostics` | 记录 ctor / OnConnection / GetCustomUI 等生命周期节点。文件日志本身出问题时，它能区分「未加载」与「已加载但日志失效」 |
| 面板回报 | 经 `client.log` 通道写入文件日志 | 面板的加载状态与布局度量。布局度量能客观判断窄栏下是否横向溢出 |

`scripts/verify-panel.ps1` 会正常启动 Excel、经窗口句柄取 `Application` 对象、调用加载项的自动化接口打开面板，然后读日志判定。

两个易踩的测试陷阱：

- **不能用 COM 自动化启动宿主**：那样启动的实例会跳过 COM 加载项。必须正常启动并带文档。
- **不要用 `GetActiveObject`**：Excel 注册到运行对象表有延迟，且调用方与 Excel 的进程完整性级别不一致时（例如从提权终端启动）根本取不到。改从 `EXCEL7` 子窗口经 `AccessibleObjectFromWindow` 取对象。

## 编码约定

同一个仓库里两类文件的 BOM 要求相反，都踩过：

- `.ps1` / `.psm1` **必须带 UTF-8 BOM**。Windows PowerShell 5.1 在无 BOM 时按系统 ANSI 代码页解读，中文会被截断导致字符串未闭合、脚本无法解析。
- JSON **绝不能带 BOM**，解析器普遍拒绝。注意 PowerShell 5.1 的 `Set-Content -Encoding UTF8` 会写入 BOM，需改用 `[System.IO.File]::WriteAllText` 配合 `UTF8Encoding($false)`。

另外路径含空格时（本仓库路径即含空格）`Start-Process -ArgumentList` 必须显式加引号，否则宿主会按空格截断参数并报「找不到文件」。
