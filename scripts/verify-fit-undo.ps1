<#
.SYNOPSIS
端到端验证「适配」按钮的撤销与恢复。

.DESCRIPTION
复现并守住一个真实缺陷：面板点「适配」后给出撤销按钮，点下去却报
「找不到该操作记录」。成因是适配省略 range、由工具自行取已用范围，
而撤销快照必须在执行前采集——旧版没有把隐式范围先解析回参数，
于是快照采集失败、没有登记记录，可加载项仍然回传了撤销标识。

因此这里断言的不只是「能撤销」，还包括：
  1. 撤销按钮出现时，它必须真的可用（点下去不报找不到记录）；
  2. 撤销后能原地恢复，两个方向都走通；
  3. 撤销确实把对齐与行高列宽还原，而不只是声称成功。

不经过模型：适配是确定性动作，面板直接调 sheet.fit。
因此本脚本不需要 mock 服务，也不消耗任何额度。
#>
[CmdletBinding()]
param(
    # 跳过部署，直接验证已安装的版本。仅在确认产物已同步时使用。
    [switch]$SkipDeploy,

    [switch]$KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$LogDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'

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

try {
    if (-not $SkipDeploy) {
        Write-Step '部署当前构建'
        & (Join-Path $PSScriptRoot 'install.ps1') -Action install -SkipBuild | Out-Null
        Write-Note '已部署最新产物'
    }
    else {
        Write-Note '按要求跳过部署，将验证已安装的版本'
    }

    Write-Step '启动 Excel 并打开面板'
    if (Test-Path -LiteralPath $LogDir) { Remove-Item -LiteralPath $LogDir -Recurse -Force }
    & (Join-Path $PSScriptRoot 'verify-panel.ps1') -Route chat -KeepOpen | Out-Null

    # 走窗口树 + 可访问性接口连上 Excel：正常启动的实例未必登记到运行对象表。
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class XlFit
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
'@ -ErrorAction SilentlyContinue

    $proc = Get-Process -Name EXCEL | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    $app = [XlFit]::Get($proc.Id)
    $automation = $app.COMAddIns.Item('ChatSheet.AddIn').Object
    $automation.ShowPane('chat')
    Start-Sleep -Seconds 2

    Write-Step '铺一片对齐参差的数据'
    # 混合对齐是关键：整片对齐统一时快照走范围级的简单路径，
    # 而用户的真实表格通常标题居中、正文靠左，走的是逐格快照那条路。
    $sheet = $app.ActiveWorkbook.Worksheets.Item(1)
    $sheet.Cells.Clear() | Out-Null

    $sheet.Range('A1:D1').Value2 = $app.WorksheetFunction.Transpose(@('名称', '数量', '单价', '备注'))
    for ($r = 2; $r -le 6; $r++) {
        $sheet.Cells.Item($r, 1).Value2 = "商品$($r - 1)"
        $sheet.Cells.Item($r, 2).Value2 = $r * 3
        $sheet.Cells.Item($r, 3).Value2 = $r * 1.5
        $sheet.Cells.Item($r, 4).Value2 = "这是一段偏长的备注文字，用来让列宽与行高有调整余地"
    }

    # -4108 = xlCenter，-4131 = xlLeft，-4160 = xlTop
    $sheet.Range('A1:D1').HorizontalAlignment = -4108
    $sheet.Range('A1:D1').VerticalAlignment = -4108
    $sheet.Range('A2:D6').HorizontalAlignment = -4131
    $sheet.Range('A2:D6').VerticalAlignment = -4160

    $beforeHeaderH = [int]$sheet.Range('A1').HorizontalAlignment
    $beforeBodyH = [int]$sheet.Range('A2').HorizontalAlignment
    $beforeBodyV = [int]$sheet.Range('A2').VerticalAlignment
    $beforeWidth = [math]::Round([double]$sheet.Columns.Item(4).ColumnWidth, 2)
    Write-Note "适配前：标题水平=$beforeHeaderH 正文水平=$beforeBodyH 正文垂直=$beforeBodyV D列宽=$beforeWidth"

    Assert-True ($beforeBodyH -eq -4131) '已造出对齐参差的范围（正文靠左、标题居中）'

    Write-Step '点击「适配」（居中）'
    $clicked = $automation.ClickFitForTest('center')
    Write-Note "点击结果：$clicked"
    # 整表适配含多次 COM 往返，给足时间。
    Start-Sleep -Seconds 4

    $notice = $automation.ReadLastNoticeForTest()
    Write-Note "提示：$notice"

    $afterHeaderH = [int]$sheet.Range('A1').HorizontalAlignment
    $afterBodyH = [int]$sheet.Range('A2').HorizontalAlignment
    $afterBodyV = [int]$sheet.Range('A2').VerticalAlignment
    $afterWidth = [math]::Round([double]$sheet.Columns.Item(4).ColumnWidth, 2)
    Write-Note "适配后：标题水平=$afterHeaderH 正文水平=$afterBodyH 正文垂直=$afterBodyV D列宽=$afterWidth"

    Assert-True ($afterBodyH -eq -4108 -and $afterBodyV -eq -4108) '适配已把正文改成水平与垂直居中'
    Assert-True ($afterWidth -ne $beforeWidth) '适配已调整列宽'

    $entry = Get-Field $notice '撤销入口'
    Assert-True ($entry -eq '撤销') "提示上出现可用的撤销入口（实际「$entry」）"

    Write-Step '点击撤销'
    # 提示上的撤销按钮与工具卡片上的同类，ClickUndoForTest 按 .tool-undo 定位。
    $undoLabel = $automation.ClickUndoForTest(0)
    Write-Note "点击了：$undoLabel"
    Start-Sleep -Seconds 4

    $log = Get-Content -LiteralPath (Join-Path $LogDir 'addin-EXCEL.log') -Raw -Encoding UTF8

    # 这是缺陷的原始症状，必须彻底消失。
    Assert-True ($log -notmatch 'NOT_FOUND') '撤销没有报「找不到该操作记录」'
    Assert-True ($log -match '撤销操作 fit-\w+：成功') '日志确认适配撤销成功'

    $undoneHeaderH = [int]$sheet.Range('A1').HorizontalAlignment
    $undoneBodyH = [int]$sheet.Range('A2').HorizontalAlignment
    $undoneBodyV = [int]$sheet.Range('A2').VerticalAlignment
    $undoneWidth = [math]::Round([double]$sheet.Columns.Item(4).ColumnWidth, 2)
    Write-Note "撤销后：标题水平=$undoneHeaderH 正文水平=$undoneBodyH 正文垂直=$undoneBodyV D列宽=$undoneWidth"

    Assert-True ($undoneBodyH -eq $beforeBodyH -and $undoneBodyV -eq $beforeBodyV) `
        '撤销还原了正文原本的靠左与顶对齐'
    Assert-True ($undoneHeaderH -eq $beforeHeaderH) '撤销保住了标题原本的居中，没被抹平'
    Assert-True ($undoneWidth -eq $beforeWidth) '撤销还原了列宽'

    $notice = $automation.ReadLastNoticeForTest()
    $entry = Get-Field $notice '撤销入口'
    Assert-True ($entry -eq '恢复') "撤销后按钮原地变为恢复（实际「$entry」）"

    Write-Step '点击恢复'
    $redoLabel = $automation.ClickUndoForTest(0)
    Write-Note "点击了：$redoLabel"
    Start-Sleep -Seconds 4

    $log = Get-Content -LiteralPath (Join-Path $LogDir 'addin-EXCEL.log') -Raw -Encoding UTF8
    Assert-True ($log -match '恢复操作 fit-\w+：成功') '日志确认适配恢复成功'
    Assert-True ($log -notmatch 'NOT_FOUND') '恢复也没有报「找不到该操作记录」'

    $redoneBodyH = [int]$sheet.Range('A2').HorizontalAlignment
    $redoneBodyV = [int]$sheet.Range('A2').VerticalAlignment
    $redoneWidth = [math]::Round([double]$sheet.Columns.Item(4).ColumnWidth, 2)
    Write-Note "恢复后：正文水平=$redoneBodyH 正文垂直=$redoneBodyV D列宽=$redoneWidth"

    Assert-True ($redoneBodyH -eq -4108 -and $redoneBodyV -eq -4108) '恢复重新把正文居中'
    Assert-True ($redoneWidth -eq $afterWidth) '恢复重新应用了适配后的列宽'

    $notice = $automation.ReadLastNoticeForTest()
    $entry = Get-Field $notice '撤销入口'
    Assert-True ($entry -eq '撤销') "恢复后按钮回到撤销（实际「$entry」）"
}
finally {
    if (-not $KeepOpen) {
        Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

Write-Host ''
if ($script:Failed -eq 0) {
    Write-Host '=== 适配撤销验证全部通过 ===' -ForegroundColor Green
    exit 0
}

Write-Host "=== 适配撤销验证有 $($script:Failed) 项失败 ===" -ForegroundColor Red
exit 1
