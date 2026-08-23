<#
.SYNOPSIS
验证设置页的本机 CLI 检测能正常出结果。

.DESCRIPTION
早先的实现有一个真实缺陷：refreshProbe 在 section 尚未 append 到文档时
就按 id 查找结果容器，取到 null 后静默返回，文字永远停在「正在检测…」。
本脚本切到模式 ① 并读取容器的实际文本，专门盯住这类回归。
#>
[CmdletBinding()]
param([switch]$KeepOpen)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SettingsPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\settings.json'
$LogDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'
$BackupSuffix = '.probe-backup'

function Write-Step { param([string]$T) Write-Host "==> $T" -ForegroundColor Cyan }
function Write-Ok { param([string]$T) Write-Host "    $T" -ForegroundColor Green }
function Write-Bad { param([string]$T) Write-Host "    $T" -ForegroundColor Red }
function Write-Note { param([string]$T) Write-Host "    $T" -ForegroundColor Yellow }

$passed = 0
$failed = 0
function Assert {
    param([string]$Label, [bool]$Ok, [string]$Detail = '')
    if ($Ok) { $script:passed++; Write-Ok $Label; return }
    $script:failed++
    $message = if ($Detail) { "$Label：$Detail" } else { $Label }
    Write-Bad $message
}

try {
    Write-Step '备份设置'
    if (Test-Path -LiteralPath $SettingsPath) {
        Copy-Item -LiteralPath $SettingsPath -Destination ($SettingsPath + $BackupSuffix) -Force
    }

    Write-Step '切到模式 ①（本机 CLI 配置）'
    $settings = [ordered]@{
        mode = 'LocalCli'
        cliSource = 'Auto'
        customProtocol = 'openai-chat-completions'
        customBaseUrl = ''
        model = ''
        thinking = 'High'
        approval = 'PerWrite'
        maxOutputTokens = 8192
        contextBudgetTokens = 100000
        maxSteps = 40
        autoIncludeSelection = $true
    }
    [System.IO.File]::WriteAllText($SettingsPath, ($settings | ConvertTo-Json -Depth 5),
        (New-Object System.Text.UTF8Encoding($true)))
    Write-Ok '已写入'

    Write-Step '部署并打开设置页'
    & (Join-Path $PSScriptRoot 'install.ps1') -Action install -SkipBuild | Out-Null
    if (Test-Path -LiteralPath $LogDir) { Remove-Item -LiteralPath $LogDir -Recurse -Force }
    & (Join-Path $PSScriptRoot 'verify-panel.ps1') -Route settings -KeepOpen | Out-Null

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class XlProbe
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder t, int m);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, sb.Capacity); return sb.ToString(); }

    public static object Get(int pid)
    {
        object result = null;
        EnumWindows((hwnd, l) => {
            int p; GetWindowThreadProcessId(hwnd, out p);
            if (p != pid || Cls(hwnd) != "XLMAIN") return true;
            EnumChildWindows(hwnd, (child, l2) => {
                if (Cls(child) != "EXCEL7") return true;
                var iid = new Guid("00020400-0000-0000-C000-000000000046");
                object w;
                if (AccessibleObjectFromWindow(child, 0xFFFFFFF0, ref iid, out w) == 0 && w != null)
                {
                    result = w.GetType().InvokeMember("Application",
                        System.Reflection.BindingFlags.GetProperty, null, w, null);
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result == null;
        }, IntPtr.Zero);
        return result;
    }
}
'@

    $proc = Get-Process -Name EXCEL | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    $app = [XlProbe]::Get($proc.Id)
    $auto = $app.COMAddIns.Item('ChatSheet.AddIn').Object

    # 打开设置页并等检测完成。
    $auto.ShowPane('settings')
    Start-Sleep -Seconds 6

    Write-Step '读取检测结果容器的实际文本'
    $text = $auto.ReadElementTextForTest('probe-result')
    Write-Ok "内容：$text"

    Assert '检测已出结果（不再停在「正在检测」）' ($text -notmatch '正在检测') $text
    Assert '容器非空' ($text -and $text.Trim().Length -gt 0)
    Assert '列出了 CLI 名称' ($text -match 'Claude CLI' -or $text -match 'Codex CLI') $text

    Write-Host ''
    Write-Host "=== 设置页检测验证：通过 $passed，失败 $failed ===" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
}
finally {
    if (-not $KeepOpen) {
        Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Stop-Process -Force
    }

    Write-Step '还原设置'
    $backup = $SettingsPath + $BackupSuffix
    if (Test-Path -LiteralPath $backup) {
        Copy-Item -LiteralPath $backup -Destination $SettingsPath -Force
        Remove-Item -LiteralPath $backup -Force
        Write-Note '已还原 settings.json'
    }
}
