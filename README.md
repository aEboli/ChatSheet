# ChatSheet

[![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![Windows](https://img.shields.io/badge/Platform-Windows-0078D4?logo=windows&logoColor=white)](#系统要求与兼容性)
[![Microsoft Excel](https://img.shields.io/badge/Host-Microsoft%20Excel-217346?logo=microsoftexcel&logoColor=white)](#系统要求与兼容性)

> 面向 Windows 桌面版 Microsoft Excel 的本地对话式 AI 侧边栏：先理解工作簿和当前选区，再在你的确认下修改表格。

ChatSheet 是一个运行在 Excel 进程中的 .NET Framework COM 加载项。它在工作簿右侧嵌入 WebView2 面板，通过原生消息桥把对话、模型流式输出、审批和表格操作连在一起；模型请求由加载项直接发送到你配置的接口，不启动 Node.js，不依赖本地 HTTP 服务、开发证书或 Office.js 旁加载。

当前版本为 [`v0.2.1`](https://github.com/aEboli/ChatSheet/releases/tag/v0.2.1)。普通 Windows 用户可从 [GitHub Release](https://github.com/aEboli/ChatSheet/releases/tag/v0.2.1) 下载预构建的 `ChatSheet-v0.2.1-win.zip`；从源码安装仍需要 .NET SDK。无论哪种安装方式，加载项日常运行本身都不需要 Node.js 或 .NET SDK。

## 为什么使用 ChatSheet

Excel 里的 AI 对话不应只是“生成一段文本”。ChatSheet 会把工作簿结构、当前选区和必要的范围内容作为上下文提供给模型，让模型通过受限的表格工具完成任务；模型触发的写入、格式、排序和结构变更默认都需要你确认，面板上的“适配”按钮则是你主动点击后直接执行的确定性排版动作。两者在对话流里用同一种操作卡片呈现，你自己点的那张带“手动”标记。

它适合这类工作：

- 解释当前选区、公式和数据异常；
- 批量整理值、公式、数字格式、列宽和行高；
- 合并或拆开单元格，例如把标题横跨几列；
- 根据已有数据新建工作表、表格或图表；
- 按指定列排序，或把自然语言要求转成可审阅的表格操作；
- 直接粘贴或拖入表格截图，让支持视觉输入的模型辅助判断问题。
- 在面板输入后点回工作表时，键盘焦点会交回 Excel；面板内输入与 Ctrl+A 仍保留原有行为。

它不把模型当作本机管理员：模型没有文件系统、命令行或任意网络访问工具；它只能调用项目公开的 Excel 工具。加载项本身仍会把你的请求发送给你选择的 AI 服务商，因此请只配置可信的服务端点。

## 核心能力

| 类别 | 当前能力 | 安全与校验行为 |
| --- | --- | --- |
| 读取与分析 | 读取工作簿结构、当前选区、指定范围的值和公式 | 单次读取最多 5,000 个单元格；超限时要求模型分批处理 |
| 写入与公式 | 写入值、写入公式、清除内容或格式 | 单次写入/清除最多 5,000 个单元格；值和公式写入后会读回实际结果 |
| 格式与数据 | 设置字体、填充、对齐、自动换行、数字格式、列宽/行高、排序 | 格式与排序最多 5,000 个单元格；自动调整最多 5,000 行或列；`fit_range`/面板“适配”是只改对齐与尺寸的例外 |
| 合并单元格 | 合并范围（可逐行合并、可顺带设置对齐）、取消合并 | 最多 5,000 个单元格，超限直接拒绝；合并前回报将丢弃几个值，撤销会把这些值找回 |
| 结构操作 | 新增或重命名工作表、创建表格、创建图表 | 默认逐项审批，并在审批卡中显示影响范围 |
| 撤销与恢复 | 对支持记录快照的操作，在操作卡中提供“撤销/恢复” | 重叠范围的乱序撤销可能产生意外结果，仍应人工复核 |
| 面板与焦点 | 面板内输入、附件操作和适配工具；点击工作表后键盘焦点交回 Excel | 面板焦点验证使用真实鼠标/键盘输入；运行验证脚本时不要操作键鼠 |
| 操作呈现 | 你点“适配”与模型发起的写入用同一种操作卡片：影响范围、撤销入口、可展开的参数与结果 | 你自己点的那张带“手动”标记并换用主色边条；颜色不单独承担区分职责 |
| 输入排队 | 任务进行中仍可输入，新消息排队并在上一轮结束后自动接着执行；排队项显示在输入区上方、与本轮用量并排，超出四条可滚动，可单独取消 | 同一时刻只跑一轮；停止会连带清空队列；被取消的内容从未发出，不在对话流里留痕 |
| 多模态输入 | 支持 PNG、JPEG、WebP 图片，直接粘贴或拖入输入框 | 每轮最多 6 张、每张不超过 5 MiB；具体模型是否支持视觉输入由服务商决定 |
| 文件附件 | 文本文件直接粘贴或拖入输入框，内容随消息发给模型 | 每轮最多 4 个、单个不超过 64 KiB、合计不超过 128 KiB；只接受文本类扩展名，xlsx/pdf 等二进制格式会被拒绝并给出替代做法 |
| 多协议模型接入 | OpenAI Chat Completions、OpenAI Responses、Anthropic Messages、Google Gemini | 支持流式文本、工具调用和模型列表发现；网关的实际兼容性仍以服务端返回为准 |
| 接入与模型选择 | 请求失败时按错误类型重试并显示进度；设置页获取的模型在对话页复用，也可手填模型 ID | 切换接入连接会清理失效的模型归属；对话页刷新是显式强制刷新 |
| 面板体验 | 记忆面板宽度；范围统一显示为“行号 × 列字母”；长模型 ID 截断不撑破布局 | 宽度受屏幕比例与合法范围约束，布局验证覆盖 300–480px 窄栏 |

## 技术架构与技术栈

```text
Microsoft Excel（桌面版）
        │
        │ COM 加载项 / Ribbon / Custom Task Pane
        ▼
ChatSheet.AddIn（C#、.NET Framework 4.8、WinForms）
        ├── Excel 自动化工具：读取、写入、格式、结构、撤销
        ├── Agent 循环：模型请求 → 工具调用 → 审批 → 执行 → 结果回传
        ├── Provider 适配：OpenAI / Anthropic / Gemini 协议
        └── Windows DPAPI：本机当前用户范围的密钥保护
        │
        │ 原生消息桥（不启本地 HTTP 服务）
        ▼
WebView2 本地侧边栏（原生 HTML + CSS + ESM）
        │
        └── HTTPS 直连你配置的 AI 服务商
```

| 层级 | 使用的技术 | 作用 |
| --- | --- | --- |
| Excel 集成 | COM 加载项、`IDTExtensibility2`、Ribbon XML、Custom Task Pane | 在 Excel 功能区注册入口并承载右侧面板 |
| 主程序 | C#、.NET Framework 4.8、WinForms、`HttpClient` | 管理 Excel COM 调用、设置、Agent 循环和模型请求 |
| 面板 | Microsoft WebView2、原生 HTML/CSS/ESM | 渲染对话、流式内容、审批卡、设置和图片/文件附件 |
| JSON 与协议 | Newtonsoft.Json 13.0.3 | 构建/解析四类模型 API 的 JSON 与流式事件 |
| 密钥保护 | Windows DPAPI `CurrentUser` | 将自定义接口密钥加密为当前用户、当前机器可解开的本地密文 |
| 安装与验证 | PowerShell、.NET 控制台测试、Node.js 测试脚本 | 注册 COM 类、诊断宿主状态、验证面板与端到端对话链路 |

WebView2 面板通过虚拟主机映射加载本地静态文件，页面的 CSP 设为 `connect-src 'none'`。也就是说，**面板网页自身不能发起网络请求**；实际模型请求只从 C# 加载项侧发出，密钥不会回传到页面。

## 系统要求与兼容性

| 项目 | 要求或状态 |
| --- | --- |
| 操作系统 | Windows；项目使用 Windows COM、注册表和 DPAPI |
| 宿主 | Microsoft Excel 桌面版 |
| 运行时 | .NET Framework 4.8、Microsoft Edge WebView2 Runtime |
| 源码安装 | .NET SDK；如果构建提示缺少 .NET Framework 引用程序集，请安装 .NET Framework 4.8 Developer Pack/Targeting Pack |
| 权限 | 安装和卸载会请求一次管理员权限，用于注册托管 COM 类；加载项登记只作用于当前 Windows 用户 |
| AI 服务 | 可用的 API 端点、模型和凭据，或含有可用 API 凭据的本机 Claude/Codex 配置 |
| Node.js | 日常安装和运行不需要；仅开发验证（前端测试或 mock 端到端测试）时需要 |

当前不支持或不承诺支持：

- **WPS 表格**：WPS 个人版不会加载本项目所需的第三方加载项，安装脚本只登记 Microsoft Excel；
- Excel for Mac、Excel 网页版：它们不支持此 Windows COM 加载项架构；
- Windows 发行包是未签名的预构建 ZIP，不是 MSI/EXE 安装器；安装和卸载仍会请求 UAC 管理员授权；
- “授权登录”设置项目前只是占位，不能替代 API 密钥或本机 CLI 配置。

## 快速开始：Windows 发行包（推荐）

从 [`v0.2.1` GitHub Release](https://github.com/aEboli/ChatSheet/releases/tag/v0.2.1) 下载以下两个资产：

- `ChatSheet-v0.2.1-win.zip`
- `ChatSheet-v0.2.1-win.zip.sha256`

先在下载目录校验 ZIP；两条命令输出的 SHA-256 值必须一致：

```powershell
Get-FileHash -Algorithm SHA256 .\ChatSheet-v0.2.1-win.zip
Get-Content .\ChatSheet-v0.2.1-win.zip.sha256
```

随后完整解压 ZIP，保存并关闭所有 Excel 窗口，在解压根目录运行：

```powershell
# 会请求 UAC 管理员授权，用于托管 COM 注册。
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Action install
```

发行包的 `app` 中已经包含构建产物，所以不调用 `dotnet build`，不需要 .NET SDK。它不是代码签名的独立 EXE/MSI；下载后请先核对哈希，并阅读随包提供的 [Windows 发行包安装说明](docs/windows-release-install.md)。

安装成功后：

1. 完全退出并重新打开 Microsoft Excel；
2. 在功能区找到 **ChatSheet** 选项卡；
3. 点击 **ChatSheet 面板**，在右侧打开面板；
4. 进入 **设置**，选择接入方式和模型；
5. 保持默认的“逐项审批”，先用一份可恢复的测试工作簿验证写入流程。

## 从源码安装（开发者）

首次安装会构建、复制、注册并进行安装后自检。执行安装前请保存工作簿；如果 Excel 正在占用已安装的旧 DLL，脚本会要求你完全退出 Excel 后再重试。

```powershell
git clone https://github.com/aEboli/ChatSheet.git
cd ChatSheet

# 会触发 UAC 管理员授权，用于 COM 注册。
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Action install
```

安装成功后：

1. 完全退出并重新打开 Microsoft Excel；
2. 在功能区找到 **ChatSheet** 选项卡；
3. 点击 **ChatSheet 面板**，在右侧打开面板；
4. 进入 **设置**，选择接入方式和模型；
5. 保持默认的“逐项审批”，先用一份可恢复的测试工作簿验证写入流程。

不要在第一次从 Git 克隆后使用 `-SkipBuild`：仓库不会提交 `bin/` 和 `obj/` 生成物。`-SkipBuild` 只适用于你已经成功构建过、且确认 `src/web` 静态资源没有比现有产物更新的情况。

## 配置模型服务

首次打开面板后，进入 **设置** 页，选择一种可用的接入方式并指定模型。

| 接入方式 | 适用场景 | 工作方式 |
| --- | --- | --- |
| 使用本机 CLI 配置 | 你的电脑已配置 Claude CLI 或 Codex CLI，并且配置文件中含可用 API 地址与令牌 | 读取 `~/.claude/settings.json` 或 `~/.codex/auth.json`；不会启动 CLI 子进程，也不会把读取到的令牌另存到 ChatSheet |
| 自定义接口 | 使用 OpenAI、Anthropic、Gemini 或兼容网关 | 填写 API 根地址、密钥、协议和模型；可尝试从服务端获取模型列表 |
| 授权登录 | 未来预留 | 当前未实现；请改用前两种方式 |

如果 Claude/Codex 使用的是订阅 OAuth 登录，而不是配置中的 API 令牌，ChatSheet 无法把该登录态直接当作接口密钥使用；请改用“自定义接口”并填写有权限的 API 凭据。

自定义接口支持的协议如下：

| 协议 | 典型请求路径 | 说明 |
| --- | --- | --- |
| OpenAI Chat Completions | `/chat/completions` | 兼容范围最广，默认选项 |
| OpenAI Responses | `/responses` | 适用于支持 Responses 协议的服务端 |
| Anthropic Messages | `/messages` | 自动添加所需的 Anthropic 认证头与版本头 |
| Google Gemini | `/models/{model}:generateContent` 或流式变体 | 模型名位于请求路径中 |

填写接口根地址时可带或不带版本段、尾斜杠或具体请求路径；程序会尝试规范化常见写法。模型列表、图片输入、流式输出、思考档位和工具调用是否真正可用，最终由你选择的模型和网关决定，不能仅凭“获取模型成功”推断全部能力可用。

### 第一次对话建议

先用只读任务确认模型配置无误：

```text
请说明当前工作簿有哪些工作表、当前选区的数据结构，以及其中可能需要清洗的问题。先不要修改任何单元格。
```

再尝试一项范围明确的写入任务：

```text
检查当前选区的日期列。将无法识别的日期列出来；确认无误后，把有效日期统一设置为 yyyy-mm-dd 格式。
```

写操作会显示审批卡，其中包含工作表、地址、行列数和单元格数等影响信息。默认选择“逐项审批”时，可选“允许”“本轮全部允许”或“拒绝”。只有在你已经用测试数据验证过模型、网关和提示词行为后，才建议考虑“全自动”。

思考档位有 Off、Minimal、Low、Medium、High、XHigh、Max 七档，界面直接用这些英文原名，与各协议的参数取值逐字一致，便于对照官方文档与请求日志。程序会根据当前协议映射到相应参数；不被目标模型支持的档位会就近降级，而不是保证服务端一定接受。

任务进行中不必等待：输入框始终可用，此时提交的内容会排队，上一轮结束后自动接着执行。排队的消息在对话流中以虚线气泡标出并显示位次，可单独取消。清空输入框后点发送按钮的位置即为停止，停止会连带取消整个队列（已排队的文字仍留在对话流里，便于复制重发）。

## 数据、隐私与安全边界

| 数据或操作 | 处理方式 |
| --- | --- |
| 自定义 API 密钥 | 使用 Windows DPAPI 以 `CurrentUser` 范围加密，保存于 `%LOCALAPPDATA%\ChatSheet\secrets\`；面板只能看到末四位掩码 |
| 普通设置 | 保存于 `%LOCALAPPDATA%\ChatSheet\settings.json`；其中不保存 API 密钥 |
| 面板网络能力 | CSP 禁止页面联网；没有本地监听端口或内置 Web 服务 |
| 发送给模型服务商的数据 | 为完成当前任务，可能包括你的提示词、工作簿结构、当前选区/读取范围内容、工具执行结果以及你主动附加的图片和文件内容 |
| 诊断日志 | 位于 `%LOCALAPPDATA%\ChatSheet\logs`；排障前后请自行审阅内容，不要将可能含业务信息的日志直接公开 |

请特别注意：

- 不要把 API key、访问令牌、真实客户数据或机密工作簿提交到 Git 仓库；
- 不要把自定义接口指向不可信网关；它能够看到你发给模型的请求内容；
- 模型输出和表格改动仍需人工审阅，尤其是财务、法律、医疗、合同、报价或需要精确公式的工作簿；
- “撤销”是辅助保护，不是备份策略。重要文件请先保存副本或使用 Excel 自身的版本管理。

## 功能边界与已知限制

| 范围 | 当前边界 |
| --- | --- |
| Excel 操作范围 | 仅提供工作簿/选区/范围读写、格式、合并、排序、工作表、表格和图表工具；没有任意文件、Shell 或网页访问工具 |
| 单次数据量 | 读取、写入、格式、清除、排序和合并均最多 5,000 单元格；`autofit_range` 最多调整 5,000 行或列；`fit_range`/面板“适配”只改对齐与行列尺寸，不受单元格数上限约束，但受快照维度和 Excel 执行时间影响 |
| 合并会丢值 | 合并只保留左上角单元格的内容，其余一律丢弃。工具会在结果中回报将丢弃几个值，撤销会把它们找回；超过 5,000 单元格时直接拒绝，而不是执行一个撤不回来的操作 |
| 图片输入 | PNG/JPEG/WebP；每轮最多 6 张、每张最多 5 MiB |
| 文件附件 | 仅文本文件（txt、md、csv、json、yaml、常见代码等）；每轮最多 4 个、单个最多 64 KiB、合计最多 128 KiB。内容整段进入上下文且不可压缩，因此总量比图片更受限。二进制格式（xlsx、docx、pdf、zip）一律拒绝——工作簿请直接在 Excel 里打开 |
| 授权登录 | 未实现 |
| 每轮确认 | UI 中存在该选项，但当前执行器尚未实现独立的“每轮预检一次”语义；在完成专项验证前，请把它视为实验性选项，使用默认逐项审批或显式点击“本轮全部允许” |
| 服务商兼容性 | 网关可能不支持模型发现、流式、工具调用、图片或特定思考参数；失败时请先用简单只读请求验证端点和模型 |
| WPS | 不支持 WPS 个人版 |

## 安装、卸载与诊断

| 命令 | 用途 |
| --- | --- |
| `.\scripts\install.ps1 -Action install` | 源码目录中会构建、安装、注册并自检；发行 ZIP 中会直接部署 `app` 预构建产物；会请求 UAC 授权 |
| `.\scripts\install.ps1 -Action install -SkipBuild` | 源码目录中仅部署已有构建产物；不适用于首次克隆，也不是发行 ZIP 的必需参数 |
| `.\scripts\install.ps1 -Action uninstall` | 反注册并删除安装目录；执行前必须完全退出 Excel；会请求 UAC 授权 |
| `.\scripts\install.ps1 -Action diagnose` | 检查 WebView2、.NET Framework、注册状态、`LoadBehavior` 和日志；只读，不需要提权 |

卸载会移除注册和 `%LOCALAPPDATA%\ChatSheet\app` 下的安装产物，但会保留 `%LOCALAPPDATA%\ChatSheet` 中的设置、密钥、WebView2 用户数据和日志；如需彻底清理，请先备份所需信息后手动删除对应目录。

常见排查顺序：

1. 完全退出 Excel 后重新打开；
2. 运行 `.\scripts\install.ps1 -Action diagnose`；
3. 若诊断显示 `LoadBehavior=2`，说明 Excel 曾因加载异常禁用该加载项；重新执行安装会重新登记加载项并清理属于 ChatSheet 的禁用项；
4. 检查 `%LOCALAPPDATA%\ChatSheet\logs` 中最新日志；
5. 如果面板提示 WebView2 初始化失败，请先安装或修复 Microsoft Edge WebView2 Runtime，再运行诊断。

## 开发与验证

### 构建

```powershell
dotnet build ChatSheet.sln -c Release
```

修改 `src/web` 中的 HTML、JavaScript 或 CSS 后必须重新构建。它们会在构建时复制到加载项输出目录；安装脚本会拒绝把较新的前端源文件与旧产物一起部署。

### 验证命令

```powershell
# 工具层与接入层验证：会启动真实 Excel 实例。
.\tests\ChatSheet.ToolTests\bin\Release\ChatSheet.ToolTests.exe

# 面板单元测试：Markdown 转义、附件分流、输入队列、发送按钮三态等。需要 Node.js。
Get-ChildItem tests\web\*.test.mjs | ForEach-Object { node $_.FullName }

# 不启动 Excel 的面板调试宿主。
.\tests\ChatSheet.PaneHarness\bin\Release\ChatSheet.PaneHarness.exe

# 正常启动 Excel，验证加载项与侧边栏是否真正加载。
.\scripts\verify-host-load.ps1 -TargetHost excel
.\scripts\verify-panel.ps1 -Route chat -KeepOpen

# 使用本地 mock 服务验证流式、工具调用、审批和读回校验。
.\scripts\verify-chat-e2e.ps1
.\scripts\verify-chat-e2e.ps1 -Approval PerWrite

# 验证任务进行中继续输入会排队、按序执行，且可取消、可随停止一并清空。
.\scripts\verify-chat-queue.ps1

# 验证面板“适配”按钮的撤销与恢复（含混合对齐的还原）。
.\scripts\verify-fit-undo.ps1

# 验证在面板打过字后点回表格，键盘焦点会交回 Excel。
# 用真实鼠标与键盘输入驱动，运行期间请勿操作键鼠。
.\scripts\verify-pane-focus.ps1
```

验证脚本可能启动、关闭，或在验证期间强制结束 Excel 进程；`verify-chat-e2e.ps1` 还会临时将 ChatSheet 设置指向本地 mock 服务并在结束时恢复设置。运行前请保存所有未保存的工作簿，并不要在生产文件上执行这些脚本。

`verify-pane-focus.ps1` 会合成鼠标与键盘输入，并在每次输入前把 Excel 抢到前台。若有其他程序持续抢占前台，脚本会直接报错终止，而不是给出不可信的结论。

## 项目结构

```text
ChatSheet/
├── src/
│   ├── ChatSheet.AddIn/       # COM 加载项、Excel 工具、模型协议、DPAPI 设置
│   └── web/                   # WebView2 侧边栏：原生 HTML、CSS、ESM
├── scripts/                   # 安装、卸载、诊断和端到端验证 PowerShell 脚本
├── tests/                     # C# 工具/协议测试、面板调试宿主、mock 服务和 Web 测试
├── docs/
│   └── architecture.md        # COM、WebView2、DPI、线程模型与诊断要点
├── work/
│   └── p0-test.xlsx           # 可复现验证使用的测试工作簿
└── ChatSheet.sln
```

如果要修改 COM 注册、Excel 宿主调用、WebView2 初始化或线程切换，请先阅读 [docs/architecture.md](docs/architecture.md)。其中记录了 COM 封送、注册表视图、Excel 禁用黑名单、DPI 和 UI 线程等容易被误判的问题。

## 发布与文档

- [v0.2.1 发行说明](docs/releases/v0.2.1.md)
- [v0.2.0 发行说明（历史版本）](docs/releases/v0.2.0.md)
- [v0.1.0 发行说明（历史版本）](docs/releases/v0.1.0.md)
- [Windows 发行包安装、校验与卸载](docs/windows-release-install.md)
- [GitHub Releases](https://github.com/aEboli/ChatSheet/releases)

## 许可证

本仓库目前尚未附带许可证。公开可见不等于授予自由复制、修改或分发的许可；如需将其作为开源项目复用，请先与维护者确认许可证安排。
