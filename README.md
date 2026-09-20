<div align="center">

# ChatSheet

**把 AI 放进 Excel 侧边栏，先看懂工作簿，再在你确认后动手。**

本地运行的 Windows Excel COM 加载项 · WebView2 面板 · 流式对话 · 可审阅的表格操作

[![Latest release](https://img.shields.io/github/v/release/aEboli/ChatSheet?display_name=tag&sort=semver)](https://github.com/aEboli/ChatSheet/releases)
[![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D4?logo=windows&logoColor=white)](#系统要求与兼容性)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?logo=dotnet&logoColor=white)](#从源码构建)
[![Excel](https://img.shields.io/badge/Microsoft%20Excel-desktop-217346?logo=microsoftexcel&logoColor=white)](#系统要求与兼容性)

[立即下载 v0.10.3.12](https://github.com/aEboli/ChatSheet/releases/tag/v0.10.3.12) · [查看发行说明](docs/releases/v0.10.3.12.md) · [提交问题](https://github.com/aEboli/ChatSheet/issues)

</div>

> [!TIP]
> 当前公开版本是 **v0.10.3.12**。普通用户下载 Windows ZIP 即可安装，不需要 .NET SDK、Node.js、Office.js 旁加载或本地 HTTP 服务。

## ChatSheet 解决什么问题

Excel 里的 AI 不应该只返回一段文字。ChatSheet 会把当前工作簿、选区和必要的范围内容交给模型，让模型通过受限的 Excel 工具完成读取、分析和修改；写入、格式、排序和结构变化会先显示影响范围，默认等你审批。

```text
选择工作簿或选区
        │
        ▼
面板理解上下文 → 模型流式回答 → 生成可审阅的操作卡片
                                      │
                         你批准后才写入 Excel
```

它适合解释公式、整理数据、批量改格式、合并标题、创建表格或图表，也适合把自然语言要求变成一组能复核、能撤销的表格操作。

## 主要能力

| 场景 | 能做什么 | 默认保护 |
| --- | --- | --- |
| 读取与分析 | 工作簿结构、当前选区、值、公式、格式和数据异常 | 单次读取有范围上限，超限时要求分批处理 |
| 写入与公式 | 写入值/公式、清除内容或格式、读回实际结果 | 写入前逐项审批，显示“现在 → 将改为” |
| 格式与数据 | 字体、填充、对齐、换行、数字格式、列宽、行高、排序 | 范围和数量受限，拒绝不连续区域 |
| 结构操作 | 新建/重命名工作表、创建表格、创建图表、合并/取消合并 | 显示影响范围；合并前提示可能丢失的值 |
| 撤销与恢复 | 还原模型操作和功能区“适配当前表”的改动 | 绑定原工作簿；可能覆盖后续修改时先警告 |
| 功能区快捷操作 | 不打开面板也能适配当前表，旁边的按钮只撤功能区操作 | 没有可撤操作时禁用，禁用按钮不会误触发 |
| 多模态输入 | 粘贴或拖入 PNG、JPEG、WebP 图片及文本附件 | 限制图片/附件数量和大小，二进制文件直接拒绝 |
| 长任务交互 | 流式输出、输入排队、停止、单独取消排队项 | 停止会取消后台网络、审批、批量探测和迟到写入 |
| 模型接入 | OpenAI Chat Completions/Responses、Anthropic、Gemini | 原生工具调用不可用时按同一审批链回退到文本协议 |
| WorkBuddy | 复用官方桌面端登录态或独立 CodeBuddy CLI | ChatSheet 不主动注销官方客户端；账号管理返回后刷新状态 |
| 主题与窄面板 | 浅色/深色主题、面板宽度记忆、300–480px 窄栏适配 | 颜色、文字对比度和焦点交互都有真实 WebView2 验证 |

## 安装

### 预构建 Windows ZIP（推荐）

1. 下载 [`ChatSheet-v0.10.3.12-win.zip`](https://github.com/aEboli/ChatSheet/releases/download/v0.10.3.12/ChatSheet-v0.10.3.12-win.zip) 和同名 `.sha256` 文件。
2. 在 PowerShell 中核对 ZIP：

   ```powershell
   Get-FileHash -Algorithm SHA256 .\ChatSheet-v0.10.3.12-win.zip
   Get-Content .\ChatSheet-v0.10.3.12-win.zip.sha256
   ```

   当前发布包 SHA-256：

   ```text
   399c2a2d129c2927a52ccc2dff6f30ddde2be6a86716377573dcc5461c7f58c9
   ```

3. 完整解压 ZIP，双击根目录的 `install.bat`，选择 `1` 安装或更新。
4. 接受 UAC 后重启 Microsoft Excel；在功能区的 **ChatSheet** 选项卡打开面板。
5. 第一次使用时，在“设置”中选择 API 接入或 WorkBuddy 授权模式和模型。

安装器会把文件部署到 `%LOCALAPPDATA%\ChatSheet\app`，注册 Excel/WPS 的 COM 加载项，并在安装后运行自检。覆盖升级前请保存并完全退出 Excel/WPS，避免旧 DLL 被宿主占用。

完整的校验、诊断、升级和卸载说明见 [Windows 发行包安装指南](docs/windows-release-install.md)。

### 从源码构建

需要 Windows、.NET Framework 4.8 Developer Pack、.NET SDK 和 WebView2 Runtime：

```powershell
dotnet build ChatSheet.sln --configuration Release
.\scripts\install.ps1 -Action install
```

源码安装会先构建再部署；预构建 ZIP 直接使用 `app\` 中的产物，不需要 SDK。安装和卸载需要管理员授权，日常运行不需要管理员权限。

## 接入方式

| 模式 | 适用情况 | 凭据处理 |
| --- | --- | --- |
| 自定义 API | 使用 OpenAI、Anthropic、Gemini 或兼容网关 | API Key 用 Windows DPAPI 按当前用户保护，页面拿不到明文 |
| 本机 CLI | 使用本机已有的 Codex/Claude 配置 | 加载项调用对应 CLI 的配置和模型目录 |
| WorkBuddy 授权 | 使用官方桌面端或国际版独立 CodeBuddy CLI | 复用官方登录态；切换账号由官方客户端完成，返回后面板自动刷新 |

面板网页只负责显示和发送消息桥事件，模型网络请求由 C# 加载项发出。页面不提供文件系统、命令行或任意网络工具；请只配置可信的服务端点，并遵守服务商的数据和计费政策。

## 安全与可靠性边界

- 模型只能调用项目公开的 Excel 工具，不能直接执行 PowerShell、访问文件系统或随意联网。
- 工具会拒绝不连续范围、超过上限的读写，以及与当前工作簿不匹配的审批、撤销和恢复请求。
- 切换工作簿、停止任务或关闭面板时，迟到的审批和 UI 回调会被丢弃，避免写入同名区域或已销毁的面板。
- 设置、密钥和常用模型名单使用原子写入；模型名单跨进程更新时使用互斥保护，异常中断不会清空旧数据。
- WebView2 顶层导航仅允许 `https://chatsheet.local/...`；外部、`file:`、`data:` 和 `about:blank` 导航会被拦截。
- 侧边栏通过 WebView2 虚拟主机映射加载本地静态文件，不监听本地端口，也不需要开发证书。

## 系统要求与兼容性

| 项目 | 支持范围 |
| --- | --- |
| 操作系统 | Windows 10/11 |
| 主要宿主 | Microsoft Excel 桌面版，已验证 x86 与 x64 |
| 额外宿主 | WPS 表格 ET，已验证 x86/x64 的 12.0 Build 19823；其他版本请先诊断 |
| 运行时 | .NET Framework 4.8、Microsoft Edge WebView2 Runtime |
| 源码开发 | .NET SDK、.NET Framework 4.8 Developer Pack、Node.js（网页测试） |
| 日常运行 | 不需要 .NET SDK、Node.js、开发证书或常驻本地服务 |
| 不支持 | Excel for Mac、Excel 网页版 |

发布 ZIP 未进行代码签名，也不是 MSI/EXE。请只从 GitHub Release 下载并先核对 SHA-256。组织策略或 Windows 保护机制可能对网络下载的 ZIP/PowerShell 脚本显示提示。

## 开发与测试

先构建：

```powershell
dotnet build ChatSheet.sln --configuration Release
```

工具和协议回归会启动隐藏的 Excel 实例；运行前请保存工作簿：

```powershell
.\tests\ChatSheet.ToolTests\bin\Release\ChatSheet.ToolTests.exe
.\tests\ChatSheet.ToolTests\bin\Release\ChatSheet.ToolTests.exe --reliability-tests
.\tests\ChatSheet.ToolTests\bin\Release\ChatSheet.ToolTests.exe --project-audit-tests
```

网页测试：

```powershell
Get-ChildItem tests\web\*.test.mjs | ForEach-Object { node $_.FullName }
```

真实 WebView2 面板和宿主加载验证：

```powershell
.\tests\ChatSheet.PaneHarness\bin\Release\ChatSheet.PaneHarness.exe --panel-security
.\scripts\verify-host-load.ps1 -TargetHost excel
```

项目当前版本的完整审查结果：Release 构建通过；工具回归 **765 通过、0 失败**；可靠性回归 **26 通过**；网页、设置页、Ribbon、WorkBuddy 登录模拟和面板安全测试全部通过；OpenSpec 严格校验 **15 通过、0 失败**。

## 项目结构

```text
.
├── src/ChatSheet.AddIn/       # C# COM 加载项、Agent、工具、Provider、存储和注册入口
├── src/web/                   # WebView2 侧边栏：HTML、CSS、ESM
├── scripts/                   # 安装、打包、发布、宿主诊断和端到端验证
├── tests/                     # 工具/协议测试、PaneHarness、Web 测试和模拟服务
├── docs/                      # 架构、发行包安装说明和版本说明
├── openspec/                  # 行为规范、变更提案与归档记录
└── ChatSheet.sln
```

涉及 COM 注册、Excel/WPS 宿主调用、WebView2 初始化或线程切换时，请先阅读 [架构说明](docs/architecture.md)。规范变更按仓库内 OpenSpec 流程维护。

## 发行与文档

- [v0.10.3.12 发行说明](docs/releases/v0.10.3.12.md)
- [Windows 发行包安装、校验与卸载](docs/windows-release-install.md)
- [架构说明与常见宿主陷阱](docs/architecture.md)
- [全部 GitHub Releases](https://github.com/aEboli/ChatSheet/releases)
- [问题反馈](https://github.com/aEboli/ChatSheet/issues)

## 许可证

本仓库目前没有附带许可证。公开可见不等于授予复制、修改或分发权限；如需复用，请先与维护者确认许可证安排。
