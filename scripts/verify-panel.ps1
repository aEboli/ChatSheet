<#
.SYNOPSIS
端到端验证：启动 Excel、打开面板、确认 WebView2 与消息桥就绪。

.DESCRIPTION
必须正常启动 Excel 并带文档——用 COM 自动化启动的实例会跳过 COM 加载项。
启动后经运行对象表连上该实例，再通过加载项的自动化接口打开面板，
最后读日志与窗口树判定面板是否真正渲染。
#>
[CmdletBinding()]
param(
    [ValidateSet('chat', 'settings', 'diagnostics')]
    [string]$Route = 'chat',

    [switch]$KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$LogDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'
$Workbook = Join-Path $RepoRoot 'work\p0-test.xlsx'

function Write-Step { param([string]$T) Write-Host "==> $T" -ForegroundColor Cyan }
function Write-Ok { param([string]$T) Write-Host "    $T" -ForegroundColor Green }
function Write-Bad { param([string]$T) Write-Host "    $T" -ForegroundColor Red }
function Write-Note { param([string]$T) Write-Host "    $T" -ForegroundColor Yellow }

if (-not (Test-Path -LiteralPath $Workbook)) {
    throw "缺少测试工作簿：$Workbook"
}

Write-Step '清理环境'
Get-Process -Name 'EXCEL' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# 清掉禁用黑名单，否则此前的失败会让加载项被永久跳过。
$resiliency = 'HKCU:\Software\Microsoft\Office\16.0\Excel\Resiliency\DisabledItems'
if (Test-Path -LiteralPath $resiliency) {
    $key = Get-Item -LiteralPath $resiliency
    foreach ($name in $key.GetValueNames()) {
        $raw = $key.GetValue($name)
        if ($raw -is [byte[]]) {
            $text = [System.Text.Encoding]::Unicode.GetString($raw)
            if ($text -match 'chatsheet') {
                Remove-ItemProperty -LiteralPath $resiliency -Name $name -Force
                Write-Note "已清除禁用黑名单项 $name"
            }
        }
    }
}

$addinKey = 'HKCU:\Software\Microsoft\Office\Excel\Addins\ChatSheet.AddIn'
if (Test-Path -LiteralPath $addinKey) {
    Set-ItemProperty -LiteralPath $addinKey -Name 'LoadBehavior' -Value 3 -Type DWord
}

if (Test-Path -LiteralPath $LogDir) { Remove-Item -LiteralPath $LogDir -Recurse -Force }
Remove-Item -LiteralPath 'HKCU:\Software\ChatSheet\Diagnostics' -Recurse -Force -ErrorAction SilentlyContinue

Write-Step '启动 Excel'
$exe = 'C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE'
if (-not (Test-Path -LiteralPath $exe)) { throw "未找到 Excel：$exe" }
# 路径必须加引号：仓库路径含空格。
Start-Process -FilePath $exe -ArgumentList "`"$Workbook`"" | Out-Null

# 通过窗口句柄取 Application 对象。
#
# 不用 GetActiveObject：Excel 注册到运行对象表有延迟，且当调用方与
# Excel 的进程完整性级别不一致时（例如从提权终端启动）根本取不到。
# 从 EXCEL7 子窗口走可访问性接口拿 Window 对象再取 Application，
# 不依赖运行对象表，稳定得多。
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class XlConnect
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    const uint OBJID_NATIVEOM = 0xFFFFFFF0;

    static string Cls(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static object GetApplication(int targetPid)
    {
        object result = null;

        EnumWindows((hwnd, l) =>
        {
            int pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid != targetPid || Cls(hwnd) != "XLMAIN") return true;

            EnumChildWindows(hwnd, (child, l2) =>
            {
                if (Cls(child) != "EXCEL7") return true;

                var iid = new Guid("00020400-0000-0000-C000-000000000046"); // IDispatch
                object window;
                if (AccessibleObjectFromWindow(child, OBJID_NATIVEOM, ref iid, out window) == 0 && window != null)
                {
                    // 拿到的是 Window 对象，其 Application 属性即所需。
                    result = window.GetType().InvokeMember("Application",
                        System.Reflection.BindingFlags.GetProperty, null, window, null);
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

Write-Step '等待加载项就绪'
$app = $null
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    $proc = Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { continue }

    try {
        $app = [XlConnect]::GetApplication($proc.Id)
        if ($app) { break }
    }
    catch {
        # 窗口尚未就绪，继续等待。
    }
}

if (-not $app) { throw '无法取得 Excel Application 对象。' }
Write-Ok "已连接 Excel $($app.Version)"

$automation = $null
for ($i = 0; $i -lt 20; $i++) {
    try {
        $addin = $app.COMAddIns.Item('ChatSheet.AddIn')
        if ($addin -and $addin.Object) {
            $automation = $addin.Object
            break
        }
    }
    catch {
    }
    Start-Sleep -Seconds 1
}

if (-not $automation) {
    Write-Bad '取不到加载项自动化接口，加载项可能未成功加载'
}
else {
    Write-Ok '已取得自动化接口'

    Write-Step "打开面板（$Route）"
    $automation.ShowPane($Route)
    Start-Sleep -Seconds 6
    Write-Ok "面板可见：$($automation.IsPaneVisible)"

    # 宽度记忆验证。
    # 拖动后要能记住宽度，否则每次打开都得重新按视口反推，
    # 而反推依赖一瞬间的测量值，落点每次都不同——那正是「面板自己抽动」的来源。
    Write-Step '宽度记忆'
    $settingsPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\settings.json'
    $before = $automation.PaneWidth
    Write-Ok "当前宿主宽度 $before"

    # 改宽度等价于用户拖动：面板会收到 resize，防抖后请求存档。
    $target = [int]$before + 80
    $applied = $automation.SetPaneWidth($target)
    Write-Ok "已调整为 $applied"

    # 面板侧防抖 400ms，留足余量等它把存档请求发出来。
    Start-Sleep -Seconds 3

    if (-not (Test-Path -LiteralPath $settingsPath)) {
        Write-Bad '未找到设置文件，无法确认宽度是否记住'
    }
    else {
        $saved = (Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 |
            ConvertFrom-Json).paneWidth

        if ($null -eq $saved) { Write-Bad '设置里没有 paneWidth，宽度未被记住' }
        elseif ([int]$saved -eq [int]$applied) { Write-Ok "宽度已记住：paneWidth=$saved" }
        else { Write-Bad "记录的宽度为 $saved，期望 $applied" }
    }
}

Write-Step '加载项日志'
if (Test-Path -LiteralPath $LogDir) {
    foreach ($log in @(Get-ChildItem -LiteralPath $LogDir -Filter '*.log')) {
        Write-Ok "文件：$($log.Name)"
        Get-Content -LiteralPath $log.FullName -Encoding UTF8 | ForEach-Object { Write-Host "      $_" }
    }
}
else {
    Write-Bad '未产生日志'
}

Write-Step '窗口树'
Add-Type -AssemblyName System.Windows.Forms | Out-Null
$found = $false
foreach ($proc in @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue)) {
    if ($proc.MainWindowTitle) {
        Write-Ok "主窗口：$($proc.MainWindowTitle)"
        $found = $true
    }
}
if (-not $found) { Write-Bad '未找到 Excel 主窗口' }

if (-not $KeepOpen) {
    Start-Sleep -Seconds 1
    Get-Process -Name 'EXCEL' -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Note '已关闭 Excel。加 -KeepOpen 可保留窗口手动查看面板。'
}
