# 安装 ChatSheet Windows 发行包

本文适用于 GitHub Release 中的 `ChatSheet-v0.2.1-win.zip`，而不是从 Git 克隆的源码目录。

> [!IMPORTANT]
> 这是带 PowerShell 安装入口的**预构建 ZIP 包**，不是 MSI 或 EXE 安装器。它目前没有代码签名；下载后请先核对 SHA-256，再决定是否运行安装脚本。

## 发行包内容

解压 `ChatSheet-v0.2.1-win.zip` 后，会得到如下目录：

```text
ChatSheet-v0.2.1-win/
├── install.bat                  # 双击打开安装菜单，输入编号选择操作
├── app/                         # 预构建的 COM 加载项、WebView2 依赖与本地面板
├── scripts/
│   ├── install.ps1              # 安装、卸载与只读诊断入口
│   ├── menu.ps1                 # install.bat 背后的交互菜单
│   └── ChatSheet.Registration.psm1
├── INSTALL.md                   # 本文副本，方便离线查阅
├── RELEASE-NOTES.md             # v0.2.1 发行说明
└── SHA256SUMS.txt               # 包内文件校验清单
```

GitHub Release 页面同时提供 `ChatSheet-v0.2.1-win.zip.sha256`，用于核对整个 ZIP 文件。

## 系统要求与支持范围

| 项目 | 要求或边界 |
| --- | --- |
| 操作系统 | Windows |
| 表格宿主 | Microsoft Excel 桌面版 |
| 必需运行时 | .NET Framework 4.8、Microsoft Edge WebView2 Runtime |
| 安装权限 | 安装和卸载时需要接受 UAC 管理员授权，用于在 HKLM 注册托管 COM 类；Excel 加载项登记仅写入当前 Windows 用户的 HKCU |
| 不需要 | .NET SDK、Node.js、Office.js 开发证书、常驻本地 HTTP 服务 |
| 不支持 | WPS 表格、Excel 网页版、Excel for Mac |

该包未经过代码签名，也不承诺静默安装、自动更新或跨用户安装。Windows 或组织策略可能对来自互联网的 ZIP 或 PowerShell 脚本显示安全提示；请只从 [ChatSheet GitHub Release](https://github.com/aEboli/ChatSheet/releases/tag/v0.2.1) 下载，并先校验哈希。

## 1. 下载并验证 SHA-256

从 [v0.2.1 Release](https://github.com/aEboli/ChatSheet/releases/tag/v0.2.1) 下载下面两个文件到同一目录：

- `ChatSheet-v0.2.1-win.zip`
- `ChatSheet-v0.2.1-win.zip.sha256`

在 PowerShell 中进入下载目录，分别读取实际哈希和发布的期望值：

```powershell
Get-FileHash -Algorithm SHA256 .\ChatSheet-v0.2.1-win.zip
Get-Content .\ChatSheet-v0.2.1-win.zip.sha256
```

两者的 64 位十六进制 SHA-256 值必须完全一致。若不一致，请不要解压或运行脚本，删除文件后重新下载。

包内的 `SHA256SUMS.txt` 用于进一步核对解压后的文件。例如，进入解压根目录后可运行：

```powershell
$expected = Get-Content .\SHA256SUMS.txt
foreach ($line in $expected) {
    $hash, $relative = $line -split '\s{2}', 2
    $actual = (Get-FileHash -Algorithm SHA256 $relative).Hash.ToLowerInvariant()
    if ($actual -ne $hash) { throw "校验失败：$relative" }
}
'包内文件 SHA-256 校验通过'
```

## 2. 解压并安装

1. 右键 ZIP，选择“全部解压缩”，保留完整的 `ChatSheet-v0.2.1-win` 目录结构；不要只复制其中一个 DLL。
2. 保存并关闭所有 Microsoft Excel 窗口。若旧版本 DLL 已被 Excel 占用，脚本会拒绝覆盖，以避免半新半旧的安装状态。
3. 双击解压根目录下的 `install.bat`，在菜单里输入 `1` 回车：

   ```text
     [1] 安装或更新    覆盖安装并注册，Excel 需重启才生效
     [2] 卸载          反注册并删除安装目录，保留日志与设置
     [3] 诊断          检查运行时、COM 注册、宿主登记与日志
     [4] 退出
   ```

   菜单顶部会显示当前安装版本、运行模式和安装位置。若更习惯命令行，在解压根目录打开 PowerShell 执行下面这条，效果完全相同：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Action install
   ```

4. Windows 将显示 UAC 提示。接受后，脚本会把 `app` 复制到 `%LOCALAPPDATA%\ChatSheet\app`，向 HKLM 的 32 位和 64 位 Classes 视图注册托管 COM 类，并在当前用户的 Excel 加载项目录登记 ChatSheet。
5. 完全重新打开 Microsoft Excel，在功能区找到 **ChatSheet** 选项卡，点击 **ChatSheet 面板**。首次使用请在“设置”中选择模型接入方式与模型。

> [!NOTE]
> 发布包的安装脚本会自动识别 `app\ChatSheet.AddIn.dll`，不会调用 `dotnet build`，因此不需要 .NET SDK。不要把源码构建流程和 ZIP 安装流程混用；从源码安装仍需要 SDK。

## 3. 诊断、升级与卸载

这三件事都可以在 `install.bat` 的菜单里做：输入 `3` 诊断、`1` 升级、`2` 卸载。菜单会提一次权，因此其中的 `diagnose` 也在管理员上下文里跑——它本身只读，不需要权限，单独用下面的命令则完全不涉及提权。

下列命令都在解压根目录中执行。`diagnose` 为只读操作，不会请求管理员权限。

| 命令 | 用途 |
| --- | --- |
| `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Action diagnose` | 检查 .NET Framework、WebView2、COM 注册、Excel 加载项登记、`LoadBehavior` 与日志 |
| `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Action install` | 安装或覆盖升级；如旧 DLL 被 Excel 占用，先完全关闭 Excel |
| `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Action uninstall` | 反注册并删除 `%LOCALAPPDATA%\ChatSheet\app`；需要先关闭 Excel，并会请求 UAC 授权 |

卸载不会自动删除 `%LOCALAPPDATA%\ChatSheet` 下的设置、DPAPI 加密密钥、WebView2 用户数据或日志；如需彻底清理，请先备份需要保留的信息后手动删除该目录。

## 4. 重要边界

- 哈希一致只能证明下载的 ZIP 和发布时的字节一致；它不等同于你的 Excel、组织策略、模型服务或工作簿一定可用。
- v0.2.1 的打包验证覆盖构建、项目测试、解包布局、包内哈希和只读诊断入口；它不替代一台全新 Windows 机器上的实际 UAC 安装验收，也不替代在你的 Excel 中的手工功能验收。
- ChatSheet 默认逐项审批写入、格式、排序和结构变化。重要工作簿仍应先备份并人工复核模型生成的修改。
- 使用模型时，完成请求所需的提示词、工作簿结构、选区/读取范围结果以及你附加的图片和文本文件内容可能会发送给你配置的服务商。请只配置可信端点，并遵守其隐私、计费和数据政策。

更多功能、安全边界与源码开发说明请见仓库根目录的 [README](../README.md) 和 [架构说明](architecture.md)。
