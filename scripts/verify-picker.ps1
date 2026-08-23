<#
.SYNOPSIS
验证模型/思考等级两列选择器，重点是「能否反复切换模型」。

.DESCRIPTION
早先的实现有一个真实缺陷：每次进入对话页都会把模型下拉重建成
只含当前模型的一项，而拉取列表的函数带一次性守卫拒绝重新获取，
结果选过一次模型后列表被摧毁且无法恢复——表现为「选了一个就不能切换」。

本脚本连续切换三次模型并核对每次的实际生效值，专门盯住这类回归。
用 mock 服务提供模型列表，因此不消耗真实额度。
#>
[CmdletBinding()]
param(
    [int]$MockPort = 58941,
    [switch]$KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SettingsPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\settings.json'
$SecretPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\secrets\custom-api-token.bin'
$LogDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'
$BackupSuffix = '.picker-backup'

function Write-Step { param([string]$T) Write-Host "==> $T" -ForegroundColor Cyan }
function Write-Ok { param([string]$T) Write-Host "    $T" -ForegroundColor Green }
function Write-Bad { param([string]$T) Write-Host "    $T" -ForegroundColor Red }
function Write-Note { param([string]$T) Write-Host "    $T" -ForegroundColor Yellow }

$passed = 0
$failed = 0
function Assert {
    param([string]$Label, [bool]$Ok, [string]$Detail = '')

    if ($Ok) {
        $script:passed++
        Write-Ok $Label
        return
    }

    $script:failed++
    $message = if ($Detail) { "$Label：$Detail" } else { $Label }
    Write-Bad $message
}

$mockJob = $null

try {
    Write-Step '备份用户配置'
    foreach ($p in @($SettingsPath, $SecretPath)) {
        if (Test-Path -LiteralPath $p) {
            Copy-Item -LiteralPath $p -Destination ($p + $BackupSuffix) -Force
        }
    }
    Write-Ok '已备份'

    Write-Step "启动 mock 服务（端口 $MockPort）"
    $mockScript = Join-Path $RepoRoot 'tests\mock-provider\server.mjs'
    $mockJob = Start-Process -FilePath 'node' -ArgumentList "`"$mockScript`" $MockPort tool" `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $env:TEMP 'chatsheet-mock-picker.log')
    Start-Sleep -Seconds 2
    Write-Ok 'mock 就绪'

    Write-Step '写入指向 mock 的设置'
    $settings = [ordered]@{
        mode = 'CustomApi'
        cliSource = 'Auto'
        customProtocol = 'openai-chat-completions'
        customBaseUrl = "http://127.0.0.1:$MockPort/v1"
        model = 'mock-model'
        thinking = 'High'
        approval = 'Automatic'
        maxOutputTokens = 8192
        contextBudgetTokens = 100000
        maxSteps = 40
        autoIncludeSelection = $true
    }
    [System.IO.File]::WriteAllText($SettingsPath, ($settings | ConvertTo-Json -Depth 5),
        (New-Object System.Text.UTF8Encoding($true)))

    Add-Type -AssemblyName System.Security
    $cipher = [System.Security.Cryptography.ProtectedData]::Protect(
        [System.Text.Encoding]::UTF8.GetBytes('mock-token'),
        [System.Text.Encoding]::UTF8.GetBytes('ChatSheet.SecretStore.v1'),
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    New-Item -ItemType Directory -Path (Split-Path $SecretPath) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($SecretPath, $cipher)
    Write-Ok "已指向 http://127.0.0.1:$MockPort/v1"

    Write-Step '部署并启动面板'
    & (Join-Path $PSScriptRoot 'install.ps1') -Action install -SkipBuild | Out-Null
    if (Test-Path -LiteralPath $LogDir) { Remove-Item -LiteralPath $LogDir -Recurse -Force }
    & (Join-Path $PSScriptRoot 'verify-panel.ps1') -Route chat -KeepOpen | Out-Null

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class XlPick
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
    $app = [XlPick]::Get($proc.Id)
    $auto = $app.COMAddIns.Item('ChatSheet.AddIn').Object

    Write-Step '展开选择器并读取两列'
    Write-Ok ("展开：" + $auto.DrivePickerForTest('open'))
    # 模型列表是异步拉取的，等它返回。
    Start-Sleep -Seconds 4

    $models = $auto.DrivePickerForTest('models')
    $thinkings = $auto.DrivePickerForTest('thinkings')
    Write-Ok "模型列：$models"
    Write-Ok "档位列：$thinkings"

    Assert '模型列有内容' ($models -and $models -notmatch '^(点击|接口未|正在)')
    Assert '档位列有七档' (($thinkings -split '\|').Count -eq 7) $thinkings

    Write-Step '连续切换模型三次（专盯「选一次就不能再切」）'
    # mock 提供 mock-model 与 mock-model-mini 两个模型，来回切换。
    $sequence = @('mock-model-mini', 'mock-model', 'mock-model-mini')
    foreach ($target in $sequence) {
        $result = $auto.DrivePickerForTest("pick-model:$target")
        Start-Sleep -Milliseconds 800
        $state = $auto.DrivePickerForTest('state')
        Assert "切换到 $target" ($result -match '已选择' -and $state -match [regex]::Escape($target)) "$result / $state"
    }

    Write-Step '切换思考等级'
    foreach ($level in @('低', '最大', '关闭思考')) {
        $result = $auto.DrivePickerForTest("pick-thinking:$level")
        Start-Sleep -Milliseconds 600
        $state = $auto.DrivePickerForTest('state')
        Assert "切换思考等级到 $level" ($result -match '已选择') "$result / $state"
    }

    Write-Step '核对已落盘的设置'
    $saved = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert '模型已持久化' ($saved.model -eq 'mock-model-mini') "实际 $($saved.model)"
    Assert '思考等级已持久化' ($saved.thinking -eq 'Off') "实际 $($saved.thinking)"

    Write-Step '收起选择器'
    Write-Ok ("收起：" + $auto.DrivePickerForTest('close'))
    $state = $auto.DrivePickerForTest('state')
    Assert '收起后浮层不可见' ($state -match '展开=false') $state

    Write-Host ''
    Write-Host "=== 选择器验证：通过 $passed，失败 $failed ===" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
}
finally {
    if (-not $KeepOpen) {
        Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Stop-Process -Force
    }

    if ($mockJob -and -not $mockJob.HasExited) {
        Stop-Process -Id $mockJob.Id -Force -ErrorAction SilentlyContinue
    }

    Write-Step '还原用户配置'
    foreach ($p in @($SettingsPath, $SecretPath)) {
        $backup = $p + $BackupSuffix
        if (Test-Path -LiteralPath $backup) {
            Copy-Item -LiteralPath $backup -Destination $p -Force
            Remove-Item -LiteralPath $backup -Force
            Write-Note "已还原 $(Split-Path $p -Leaf)"
        }
        elseif (Test-Path -LiteralPath $p) {
            Remove-Item -LiteralPath $p -Force
        }
    }
}
