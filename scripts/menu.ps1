<#
.SYNOPSIS
ChatSheet 安装器的交互菜单：输入编号选择安装、卸载或诊断。

.DESCRIPTION
给不用命令行的人一个入口。install.ps1 仍是真正干活的脚本，这里只负责问清楚
要做哪件事，然后在同一个控制台里把它跑起来。

为什么先自我提权：注册托管 COM 类要写 HKLM，install.ps1 未提权时会经 UAC
重启自身并在结束后退出整个进程——菜单会跟着一起消失。菜单启动时提一次权，
之后每个动作都在同一个窗口里跑完，既不再弹 UAC，也不会跑完就没了。
诊断本身不需要管理员，但为此把菜单拆成两种权限只会多一次弹窗和一次解释。
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$InstallScript = Join-Path $PSScriptRoot 'install.ps1'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$InstallDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\app'

if (-not (Test-Path -LiteralPath $InstallScript)) {
    Write-Host "找不到安装脚本：$InstallScript" -ForegroundColor Red
    Write-Host '请保持 scripts 目录完整，不要单独移动本文件。' -ForegroundColor Yellow
    [void](Read-Host '按回车退出')
    exit 1
}

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 未提权时带着同样的参数重启自身，然后退出当前这个没有权限的实例。
if (-not (Test-Elevated)) {
    Write-Host '安装与卸载需要管理员权限，正在请求授权…' -ForegroundColor Yellow
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
            '-NoProfile'
            '-ExecutionPolicy', 'Bypass'
            '-File', "`"$PSCommandPath`""
        ) | Out-Null
    }
    catch {
        Write-Host "提权被取消，无法继续：$($_.Exception.Message)" -ForegroundColor Red
        [void](Read-Host '按回车退出')
        exit 1
    }

    exit 0
}

<# 当前安装状态。任何人打开安装器最先想知道的就是这个。 #>
function Get-InstalledSummary {
    $dll = Join-Path $InstallDir 'ChatSheet.AddIn.dll'
    if (-not (Test-Path -LiteralPath $dll)) {
        return '未安装'
    }

    try {
        $version = (Get-Item -LiteralPath $dll).VersionInfo.FileVersion
        $stamp = (Get-Item -LiteralPath $dll).LastWriteTime.ToString('yyyy-MM-dd HH:mm')
        return "已安装 $version（$stamp）"
    }
    catch {
        return '已安装（版本读取失败）'
    }
}

<#
运行模式决定「安装」会不会先构建。
源码检出里有 csproj，install.ps1 会先 dotnet build；发行包里只有 app\ 预构建产物。
写出来是因为这两种模式对「安装」意味着不同的耗时和前置条件。
#>
function Get-LayoutSummary {
    if (Test-Path -LiteralPath (Join-Path $RepoRoot 'src\ChatSheet.AddIn\ChatSheet.AddIn.csproj')) {
        return '源码检出（安装前会先构建，需要 .NET SDK）'
    }
    if (Test-Path -LiteralPath (Join-Path $RepoRoot 'app\ChatSheet.AddIn.dll')) {
        return '预构建发行包（不需要 .NET SDK）'
    }
    return '无法判断（既没有 csproj 也没有 app\ 产物）'
}

function Show-Menu {
    Write-Host ''
    Write-Host '==================================================' -ForegroundColor Cyan
    Write-Host '  ChatSheet 安装器' -ForegroundColor Cyan
    Write-Host '==================================================' -ForegroundColor Cyan
    Write-Host "  当前状态：$(Get-InstalledSummary)"
    Write-Host "  运行模式：$(Get-LayoutSummary)"
    Write-Host "  安装位置：$InstallDir"
    Write-Host ''
    Write-Host '  [1] 安装或更新    覆盖安装并注册，Excel 需重启才生效'
    Write-Host '  [2] 卸载          反注册并删除安装目录，保留日志与设置'
    Write-Host '  [3] 诊断          检查运行时、COM 注册、宿主登记与日志'
    Write-Host '  [4] 退出'
    Write-Host ''
}

<#
把动作交给 install.ps1。

用 & 调用而不是点源：install.ps1 里的 exit 在被调用的脚本作用域里只结束它自己，
点源则会把菜单一起带走。异常也在这里收住，一次失败不该让整个菜单退出——
用户往往正想接着去点「诊断」看看到底缺了什么。
#>
function Invoke-Action {
    param([Parameter(Mandatory)][ValidateSet('install', 'uninstall', 'diagnose')][string]$Action)

    Write-Host ''
    try {
        & $InstallScript -Action $Action
    }
    catch {
        Write-Host ''
        Write-Host "执行失败：$($_.Exception.Message)" -ForegroundColor Red
        Write-Host '可以选 [3] 诊断查看环境与注册状态。' -ForegroundColor Yellow
    }

    Write-Host ''
    [void](Read-Host '按回车返回菜单')
}

$Host.UI.RawUI.WindowTitle = 'ChatSheet 安装器'

while ($true) {
    Show-Menu
    $choice = (Read-Host '请输入选项编号（1-4）').Trim()

    switch ($choice) {
        '1' { Invoke-Action -Action 'install' }
        '2' { Invoke-Action -Action 'uninstall' }
        '3' { Invoke-Action -Action 'diagnose' }
        '4' { Write-Host ''; Write-Host '已退出。' -ForegroundColor Green; exit 0 }
        default {
            Write-Host ''
            Write-Host "「$choice」不是有效选项，请输入 1、2、3 或 4。" -ForegroundColor Yellow
        }
    }
}
