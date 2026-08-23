# ChatSheet

嵌入 Microsoft Excel 右侧面板的对话式 AI 助手。装完即用，不需要 Node.js、开发证书、SDK 或任何环境变量。

## 快速开始

```powershell
.\scripts\install.ps1 -Action install
```

安装会请求一次管理员权限（原因见下文「为什么需要提权」），然后重启 Excel，在功能区 **ChatSheet** 选项卡点击「ChatSheet 面板」。

首次使用请到面板的「设置」页选择接入模式与模型。

| 命令 | 作用 |
| --- | --- |
| `.\scripts\install.ps1 -Action install` | 构建、安装、注册，并做安装后自检 |
| `.\scripts\install.ps1 -Action install -SkipBuild` | 跳过构建直接安装已有产物 |
| `.\scripts\install.ps1 -Action uninstall` | 反注册并删除安装目录 |
| `.\scripts\install.ps1 -Action diagnose` | 检查运行时依赖与注册状态，不需要提权 |

## 三种接入模式

**① 使用本机 CLI 配置** — 读取 `~/.claude/settings.json` 或 `~/.codex/auth.json` 中的接口地址与令牌，当作普通接口直连，不启动 CLI 子进程。若这些 CLI 使用订阅登录（OAuth）而非 API 密钥，配置里不会有令牌，此时请改用模式 ②。

**② 自定义接口** — 填接口地址、密钥，可一键自动获取模型列表。支持四种协议：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages、Google Gemini。密钥用 Windows DPAPI 以当前用户范围加密保存，不会明文落盘，也不会进入面板页面。

**③ 授权登录** — 占位，尚未实现。

思考模式可选关闭或轻/中/深三档，按所选协议映射到 `reasoning_effort`、`thinking.budget_tokens` 或 `thinkingConfig`；模型不支持时自动忽略。

## 能力边界

助手**只能操作表格**，不提供文件系统、命令行或网络访问：

| 类别 | 工具 |
| --- | --- |
| 读取 | 工作簿结构、当前选区、范围值与公式 |
| 写入 | 写值、写公式、格式、数字格式、自动列宽行高、清除 |
| 结构 | 新增/重命名工作表、创建表格、创建图表 |
| 数据 | 按列排序 |

单次读取上限 2000 个单元格、写入 5000 个，超限会要求分批处理。写入后一律读回校验，把实际结果回报给模型，避免它误认为成功。

## 审批与安全

默认**写操作逐项审批、读操作自动执行**。审批卡片会显示影响范围（工作表、地址、行列数、单元格总数），可选择「允许」「本轮全部允许」或「拒绝」。设置页可切换为每轮确认一次或全自动。

- 密钥用 DPAPI 加密，面板只能拿到末四位掩码
- 面板页面的 CSP 为 `connect-src 'none'`，它不发起任何网络请求，所有接口调用都在加载项进程内完成
- 模型输出一律转义后再渲染，Markdown 链接不生成可点击的 `href`

## 上下文管理

每轮自动注入工作簿结构摘要与当前选区。超出 token 预算时先压缩较早的工具结果，仍超限则移除最早的记录并明确告知模型。面板底部实时显示本轮用量与上下文占比。

## 为什么需要提权

托管 COM 类只能注册到 HKLM——承载它的 `mscoree` 不读取 HKCU 下的类注册信息（实测 HKCU 注册时激活报 `0x80070002`）。VSTO 能免提权是因为其原生加载器本身注册在 HKLM，HKCU 项只指向该加载器。

因此安装与卸载各需一次管理员授权。加载项登记本身仍写在 HKCU，只影响当前用户。

## 关于 WPS

**WPS 表格不受支持。** 实测 WPS 个人版（12.1.0.28043）无法加载第三方加载项：

- COM 加载项：覆盖 7 个候选注册路径、白名单、HKCU/HKLM 双写，均未被实例化。用已在 Excel 验证可加载的官方 PIA 探针做对照组，同样不加载，可排除实现问题。
- JSAPI 加载项：本地目录与 HTTP 两种形式都收不到任何请求，`oem.ini` 的 `JsApiPlugin=true` 与 `forceEnabledJsAddinName` 白名单也已配置。

第三方加载项通常是专业版/企业版功能。文档操作层全程使用后期绑定 COM 编写，本就一份代码可驱动两个宿主，若日后换到专业版可直接重测。

## 开发

```powershell
dotnet build ChatSheet.sln -c Release          # 构建全部项目

# 工具层与接入层验证（会启动一个真实 Excel 实例）
.\tests\ChatSheet.ToolTests\bin\Release\ChatSheet.ToolTests.exe

# 面板渲染验证（Markdown 转义与流式）
node tests\web\markdown.test.mjs

# 不启动 Excel 也能调试面板
.\tests\ChatSheet.PaneHarness\bin\Release\ChatSheet.PaneHarness.exe

.\scripts\verify-host-load.ps1 -TargetHost excel   # 验证宿主是否加载加载项
.\scripts\verify-panel.ps1 -Route chat -KeepOpen   # 端到端打开面板

# 完整对话链路验证：用本地 mock 服务替代真实接口，
# 不消耗额度、不需要真实密钥，测完自动还原你的设置。
.\scripts\verify-chat-e2e.ps1                      # 全自动策略
.\scripts\verify-chat-e2e.ps1 -Approval PerWrite   # 逐项审批策略（默认策略）
```

`verify-chat-e2e.ps1` 覆盖流式文本、工具调用参数增量拼接、工具执行、审批交互，并读回单元格核对写入是否真实生效。它也是唯一能暴露线程模型问题的验证——工具层单元测试跑在 STA 线程上，会掩盖这类故障。

改动 `src/web` 下的文件后**必须重新构建**才会生效——这些静态文件是靠构建复制到输出目录的。安装脚本会检测源文件是否比产物新并拦住。

诊断信息在 `%LOCALAPPDATA%\ChatSheet\logs`，面板自身的状态也会回报到同一份日志。

`docs/architecture.md` 记录了实现中遇到的四个非显然陷阱及其误导性报错，改动 COM 层前建议先读。

## 目录

```
src/ChatSheet.AddIn/     COM 加载项（net48 AnyCPU）
  Interop/               手写 Office 扩展性接口声明
  Hosts/                 宿主抽象，全程后期绑定
  Bridge/                面板与加载项的消息桥
  Providers/             四协议接入、流式解析、模型发现
  Agent/                 系统提示、会话与上下文管理、工具循环
  Tools/                 表格工具集与安全上限
  Storage/               DPAPI 密钥存储与设置
src/web/                 侧边栏界面（原生 ESM，零构建）
scripts/                 安装、卸载、诊断、验证
tests/                   工具层、接入层、面板渲染验证
```
