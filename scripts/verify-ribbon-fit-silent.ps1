<#
.SYNOPSIS
端到端验证功能区「适配当前表」的静默路径。

.DESCRIPTION
守住三件事：

  1. 点快捷入口时面板保持隐藏，也不弹「面板打不开」之类的对话框；
  2. 当前表确实被排好（水平垂直居中、列宽有变化）；
  3. 之后打开面板，对话流里有一张手动适配卡片——撤销入口必须还在，
     因为加载项经 COM 的写入会清空 Excel 自己的撤销栈。

功能区按钮只能由真实点击触发，脚本点不了。自动化接口
FitCurrentSheetForTest 调用与功能区回调同一个方法，不另写一份实现。

必须正常启动 Excel 并带文档——用 COM 自动化启动的实例会跳过 COM 加载项。
#>
[CmdletBinding()]
param(
    [switch]$SkipDeploy,
    [switch]$KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$LogDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'
$Workbook = Join-Path $RepoRoot 'work\p0-test.xlsx'

function Write-Step { param([string]$T) Write-Host "==> $T" -ForegroundColor Cyan }
function Write-Ok { param([string]$T) Write-Host "    通过  $T" -ForegroundColor Green }
function Write-Bad { param([string]$T) Write-Host "    失败  $T" -ForegroundColor Red }
function Write-Note { param([string]$T) Write-Host "    $T" -ForegroundColor Yellow }

$script:Failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Ok $Message }
    else { Write-Bad $Message; $script:Failed++ }
}

function Get-Field {
    param([string]$State, [string]$Name)
    foreach ($part in ($State -split '\|')) {
        $pair = $part.Trim()
        if ($pair -like "$Name=*") { return $pair.Substring($Name.Length + 1) }
    }

    return ''
}

function Get-LogText {
    $path = Join-Path $LogDir 'addin-EXCEL.log'
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

try {
    if (-not (Test-Path -LiteralPath $Workbook)) {
        throw "缺少测试工作簿：$Workbook"
    }

    if (-not $SkipDeploy) {
        Write-Step '部署当前构建'
        & (Join-Path $PSScriptRoot 'install.ps1') -Action install -SkipBuild | Out-Null
        Write-Note '已部署最新产物'
    }
    else {
        Write-Note '按要求跳过部署，将验证已安装的版本'
    }

    Write-Step '清理环境'
    Get-Process -Name 'EXCEL' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2

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

    Write-Step '启动 Excel（不打开面板）'
    $exe = 'C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE'
    if (-not (Test-Path -LiteralPath $exe)) { throw "未找到 Excel：$exe" }
    Start-Process -FilePath $exe -ArgumentList "`"$Workbook`"" | Out-Null

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class XlSilentFit
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder t, int m);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    static string Cls(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

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

    $app = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $proc = Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc) { continue }
        try {
            $app = [XlSilentFit]::Get($proc.Id)
            if ($app) { break }
        }
        catch { }
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
        catch { }
        Start-Sleep -Seconds 1
    }

    if (-not $automation) { throw '取不到加载项自动化接口，加载项可能未成功加载' }
    Write-Ok '已取得自动化接口'
    Assert-True (-not $automation.IsPaneVisible) '启动后面板默认不可见'

    Write-Step '铺一片对齐参差的数据'
    $sheet = $app.ActiveWorkbook.Worksheets.Item(1)
    $sheet.Cells.Clear() | Out-Null
    $sheet.Range('A1:D1').Value2 = $app.WorksheetFunction.Transpose(@('名称', '数量', '单价', '备注'))
    for ($r = 2; $r -le 6; $r++) {
        $sheet.Cells.Item($r, 1).Value2 = "商品$($r - 1)"
        $sheet.Cells.Item($r, 2).Value2 = $r * 3
        $sheet.Cells.Item($r, 3).Value2 = $r * 1.5
        $sheet.Cells.Item($r, 4).Value2 = "这是一段偏长的备注文字，用来让列宽与行高有调整余地"
    }

    $sheet.Range('A1:D1').HorizontalAlignment = -4108
    $sheet.Range('A1:D1').VerticalAlignment = -4108
    $sheet.Range('A2:D6').HorizontalAlignment = -4131
    $sheet.Range('A2:D6').VerticalAlignment = -4160

    $beforeBodyH = [int]$sheet.Range('A2').HorizontalAlignment
    $beforeBodyV = [int]$sheet.Range('A2').VerticalAlignment
    $beforeWidth = [math]::Round([double]$sheet.Columns.Item(4).ColumnWidth, 2)
    Write-Note "适配前：正文水平=$beforeBodyH 正文垂直=$beforeBodyV D列宽=$beforeWidth"
    Assert-True ($beforeBodyH -eq -4131) '已造出正文靠左的范围'

    Write-Step '走功能区静默路径（不打开面板）'
    $automation.FitCurrentSheetForTest()

    # 冷启动要等 WebView2 初始化 + 页面加载 + 整表适配。有界重试上限 10 秒，
    # 再给适配本身几秒。面板保持隐藏时初始化可能比显示时慢，所以给足。
    $aligned = $false
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Seconds 1
        try {
            $nowH = [int]$sheet.Range('A2').HorizontalAlignment
            $nowV = [int]$sheet.Range('A2').VerticalAlignment
            if ($nowH -eq -4108 -and $nowV -eq -4108) {
                $aligned = $true
                break
            }
        }
        catch { }
    }

    $afterBodyH = [int]$sheet.Range('A2').HorizontalAlignment
    $afterBodyV = [int]$sheet.Range('A2').VerticalAlignment
    $afterWidth = [math]::Round([double]$sheet.Columns.Item(4).ColumnWidth, 2)
    Write-Note "适配后：正文水平=$afterBodyH 正文垂直=$afterBodyV D列宽=$afterWidth 等待=$i 秒"
    Write-Note "面板可见：$($automation.IsPaneVisible)"

    Assert-True (-not $automation.IsPaneVisible) '适配期间面板保持隐藏'
    Assert-True $aligned '静默路径已把正文改成水平与垂直居中'
    Assert-True ($afterWidth -ne $beforeWidth) '静默路径已调整列宽'

    $log = Get-LogText
    Assert-True ($log -notmatch '准备显示成因提示') '没有弹出「面板打不开」对话框'
    Assert-True ($log -notmatch 'OnTogglePane') '没有走打开面板的功能区路径'
    Assert-True ($log -match '功能区适配：已触发面板的适配动作') '日志确认点到了面板的适配按钮'

    Write-Step '打开面板核对着操作卡片'
    $automation.ShowPane('chat')
    Start-Sleep -Seconds 4
    Assert-True $automation.IsPaneVisible '此刻才打开面板'

    $card = $null
    for ($i = 0; $i -lt 10; $i++) {
        $card = $automation.ReadLastToolCardForTest()
        if ($card -and (Get-Field $card '名称') -eq '适配') { break }
        Start-Sleep -Seconds 1
    }

    Write-Note "操作卡片：$card"
    Assert-True ((Get-Field $card '名称') -eq '适配') "打开面板后能看到适配卡片（实际「$(Get-Field $card '名称')」）"
    Assert-True ((Get-Field $card '来源') -eq '手动') '卡片来源是手动'
    Assert-True ((Get-Field $card '标记') -eq '手动') '卡片摘要行带「手动」标记'
    Assert-True ((Get-Field $card '撤销入口') -eq '撤销') "卡片上有可用的撤销入口（实际「$(Get-Field $card '撤销入口')」）"
}
catch {
    Write-Bad $_.Exception.Message
    $script:Failed++
}
finally {
    if (-not $KeepOpen) {
        Get-Process -Name 'EXCEL' -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

Write-Host ''
if ($script:Failed -eq 0) {
    Write-Host '=== 功能区静默适配：全部通过 ===' -ForegroundColor Green
    exit 0
}

Write-Host "=== 功能区静默适配：失败 $script:Failed 项 ===" -ForegroundColor Red
exit 1
