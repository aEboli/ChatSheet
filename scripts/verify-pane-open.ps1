<#
.SYNOPSIS
验证「面板打不开」的成因判定、自愈与提示，全程走真实功能区点击。

.DESCRIPTION
四项检查：
  1. 只读工作簿上面板照常打开（只读不是成因，不该拦住面板也不该弹提示）
  2. 连续开关不重建面板（父窗口比对若误判，每次点击都会重建，丢掉对话内容）
  3. SDI：面板挂在别的工作簿窗口上时，就地重建到当前窗口
  4. 制造真实失败（暂时断开 ProgID → CLSID 映射），确认弹出提示且文案正确

必须走真实点击，不能用自动化接口：后者刻意传 interactive=false 以免挂住脚本，
因此永远不弹提示，验不到本次要交付的东西。

本机环境的三个坑（脚本已处理，改动时勿踩回去）：
  - 提权环境下 GetActiveObject 附加不上 Excel（提权的 Office 不在 ROT 里登记），
    所以全程用 UI 自动化，不用 COM 附加。
  - 屏幕 150% 缩放。进程不声明 DPI 感知时看到的桌面是缩放后的，
    UIA 报的却是物理像素，截图与点击都会整体偏移。
  - 抓提示框不能用 UIA：Excel 主线程正卡在模态框的嵌套消息循环里，
    对该进程窗口的 UIA 属性读取会超时抛异常，一吞就等于没看见。
    用 Win32 EnumWindows。

.PARAMETER ReadOnlyWorkbook
用于第 1 项的只读工作簿。默认用仓库内的 fixture 复制一份并置只读。

.PARAMETER SkipFailureInjection
跳过第 4 项。该项会临时改 HKLM 下两个 Classes 视图的 ProgID 映射，
脚本在 finally 里无条件还原并复验；不希望动注册表时用此开关。
#>
[CmdletBinding()]
param(
    [string]$ReadOnlyWorkbook = '',
    [switch]$SkipFailureInjection
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$LogPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs\addin-EXCEL.log'
$Fixture = Join-Path $RepoRoot 'work\p0-test.xlsx'
$Clsid = '{0417A068-632B-4CAD-9390-3479277B03CB}'
$ProgIdKeys = @(
    'HKLM:\SOFTWARE\Classes\ChatSheet.TaskPane\CLSID',
    'HKLM:\SOFTWARE\WOW6432Node\Classes\ChatSheet.TaskPane\CLSID'
)

$script:pass = 0
$script:fail = 0
function Head { param([string]$T) Write-Host "`n=== $T ===" -ForegroundColor Cyan }
function Say  { param([string]$T) Write-Host "  $T" -ForegroundColor DarkGray }
function Chk  { param([string]$N, [bool]$C, [string]$D = '')
    if ($C) { $script:pass++; Write-Host "  [通过] $N" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  [失败] $N  $D" -ForegroundColor Red } }

# ---- Win32：抓模态框用，不依赖 UIA ----
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class PaneVerifyWin32
{
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static string Text(IntPtr h) { var sb = new StringBuilder(2048); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString(); }
    public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, sb.Capacity); return sb.ToString(); }
    public static List<IntPtr> FindTop(string needle)
    {
        var hits = new List<IntPtr>();
        EnumWindows((h, p) => { if (IsWindowVisible(h) && Text(h).Contains(needle)) { hits.Add(h); } return true; }, IntPtr.Zero);
        return hits;
    }
    public static List<string> Children(IntPtr parent)
    {
        var list = new List<string>();
        EnumChildWindows(parent, (h, p) => { list.Add(Cls(h) + "\u0001" + Text(h)); return true; }, IntPtr.Zero);
        return list;
    }
}
'@
[PaneVerifyWin32]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Get-ExcelExe {
    foreach ($c in @(
        'C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE',
        'C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE')) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    throw '找不到 EXCEL.EXE'
}
$ExcelExe = Get-ExcelExe

function Stop-Excel {
    Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# 启动后必须显式还原窗口：最小化时功能区不在 UIA 树里，
# 面板还会拿到一个退化的视口（实测 69 CSS 像素），
# 触发既有的宽度校准把宿主宽度放大并落盘。
function Start-Excel {
    param([string]$File)
    Start-Process -FilePath $ExcelExe -ArgumentList "`"$File`""
    Start-Sleep -Seconds 8
    $p = Get-Process -Name EXCEL -ErrorAction SilentlyContinue |
        Sort-Object StartTime -Descending | Select-Object -First 1
    if (-not $p) { throw 'Excel 未启动' }
    [PaneVerifyWin32]::ShowWindow($p.MainWindowHandle, 3) | Out-Null
    Start-Sleep -Milliseconds 900
    [PaneVerifyWin32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 2
    return $p
}

function Get-WindowByTitle {
    param([string]$Pattern)
    foreach ($p in (Get-Process -Name EXCEL -ErrorAction SilentlyContinue)) {
        foreach ($w in $AE::RootElement.FindAll($TS::Children,
            (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$p.Id)))) {
            if ($w.Current.ClassName -ne 'XLMAIN') { continue }
            if ($w.Current.Name -match $Pattern) { return $w }
        }
    }
    return $null
}

function Focus-Window {
    param($W)
    try {
        $h = [IntPtr]$W.Current.NativeWindowHandle
        [PaneVerifyWin32]::ShowWindow($h, 3) | Out-Null
        Start-Sleep -Milliseconds 500
        [PaneVerifyWin32]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Milliseconds 1200
    } catch { Say "聚焦失败：$($_.Exception.Message)" }
}

function Click-PaneButton {
    param($Win)
    if (-not $Win) { return 'no-window' }
    $n = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'ChatSheet')
    $t = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::TabItem)
    $tab = $Win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition($n, $t)))
    if (-not $tab) { return 'no-tab' }
    try { $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
          Start-Sleep -Milliseconds 800 } catch {}
    $btn = $Win.FindFirst($TS::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'ChatSheet 面板')))
    if (-not $btn) { return 'no-button' }
    try { $btn.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return 'Toggle' } catch {}
    try { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return 'Invoke' } catch {}
    return 'none'
}

# 窗格在宿主里是类名 MsoWorkPane、标题为 ChatSheet 的 Window。
# 只看在不在场不够，隐藏时它仍在树里，要看 IsOffscreen。
function Test-PaneVisible {
    param($Win)
    if (-not $Win) { return $false }
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'ChatSheet')),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'MsoWorkPane')))
    $wp = $Win.FindFirst($TS::Descendants, $c)
    return ($null -ne $wp) -and (-not $wp.Current.IsOffscreen)
}

function Get-LogMark { if (Test-Path $LogPath) { (Get-Item $LogPath).Length } else { 0 } }
function Get-LogSince {
    param([long]$Offset)
    if (-not (Test-Path $LogPath)) { return '' }
    $fs = [System.IO.File]::Open($LogPath, 'Open', 'Read', 'ReadWrite')
    try {
        $fs.Seek($Offset, 'Begin') | Out-Null
        return (New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)).ReadToEnd()
    } finally { $fs.Close() }
}
function Show-LogLines {
    param([string]$Text)
    $Text.Split("`n") | Where-Object { $_ -match 'OnTogglePane|创建成功|挂在其他|不一致|重建|成因|提示' } |
        ForEach-Object { Say $_.Trim() }
}

# ---- 准备只读工作簿 ----
if (-not $ReadOnlyWorkbook) {
    if (-not (Test-Path -LiteralPath $Fixture)) { throw "缺少 fixture：$Fixture" }
    $ReadOnlyWorkbook = Join-Path $RepoRoot 'work\verify-readonly.xlsx'
    Copy-Item -LiteralPath $Fixture -Destination $ReadOnlyWorkbook -Force
    Set-ItemProperty -LiteralPath $ReadOnlyWorkbook -Name IsReadOnly -Value $true
    $script:madeReadOnly = $true
} else {
    $script:madeReadOnly = $false
}

try {
    Stop-Excel

    # ---------- 1. 只读工作簿 ----------
    Head '1. 只读工作簿上打开面板'
    $proc = Start-Excel $ReadOnlyWorkbook
    Say "标题：$($proc.MainWindowTitle)"
    $winA = Get-WindowByTitle '.'
    $mark = Get-LogMark
    Say "点击：$(Click-PaneButton $winA)"
    Start-Sleep -Seconds 5
    $log1 = Get-LogSince $mark
    Chk '只读工作簿上面板能打开' (Test-PaneVisible $winA) '面板没出现'
    Chk 'WebView2 初始化成功' ($log1 -match 'WebView2 初始化成功') ''
    Chk '未判定成因（只读本不该拦住面板）' (-not ($log1 -match '面板未能打开')) ''
    Chk '未弹提示' (([PaneVerifyWin32]::FindTop('打不开')).Count -eq 0) ''
    $vp = [regex]::Match($log1, '视口 (\d+)x').Groups[1].Value
    if ($vp) { Chk "面板视口正常（$vp CSS 像素，退化值是 69）" ([int]$vp -gt 200) "视口 $vp" }
    Show-LogLines $log1

    # ---------- 2. 连续开关不重建 ----------
    Head '2. 连续开关四次，不应重建面板'
    $mark = Get-LogMark
    for ($i = 1; $i -le 4; $i++) {
        Click-PaneButton $winA | Out-Null
        Start-Sleep -Seconds 3
    }
    $log2 = Get-LogSince $mark
    $creates = ([regex]::Matches($log2, '侧边栏创建成功')).Count
    $orphans = ([regex]::Matches($log2, '挂在其他工作簿窗口上')).Count
    Say "创建次数=$creates 误判跨窗口=$orphans"
    Chk '开关过程中没有重建面板' ($creates -eq 0) "创建了 $creates 次——会丢对话内容"
    Chk '没有把同一个窗口误判成别的窗口' ($orphans -eq 0) "误判 $orphans 次"

    # ---------- 3. SDI 跨窗口 ----------
    Head '3. SDI：在另一个工作簿的窗口里点面板'
    if (-not (Test-PaneVisible $winA)) { Click-PaneButton $winA | Out-Null; Start-Sleep -Seconds 4 }
    Start-Process -FilePath $ExcelExe -ArgumentList "`"$Fixture`""
    Start-Sleep -Seconds 8
    $winB = Get-WindowByTitle 'p0-test'
    if (-not $winB) {
        Chk '打开第二个工作簿' $false '找不到它的窗口'
    } else {
        Chk '第二个窗口里本来没有面板（窗格绑在第一个窗口上）' (-not (Test-PaneVisible $winB)) ''
        Focus-Window $winB
        $mark = Get-LogMark
        Say "点击：$(Click-PaneButton $winB)"
        Start-Sleep -Seconds 5
        $log3 = Get-LogSince $mark
        Chk '第二个窗口里面板出现了' (Test-PaneVisible $winB) '面板没在第二个窗口出现'
        Chk '识别出跨窗口并重建' ($log3 -match '挂在其他工作簿窗口') ''
        Show-LogLines $log3
    }

    # ---------- 4. 真实失败下的提示 ----------
    if ($SkipFailureInjection) {
        Head '4. 提示框（已按开关跳过）'
    } else {
        Head '4. 制造真实失败，确认弹出提示'
        $orig = @{}
        foreach ($k in $ProgIdKeys) { $orig[$k] = (Get-ItemProperty -LiteralPath $k).'(default)' }
        try {
            Stop-Excel
            foreach ($k in $ProgIdKeys) {
                Set-ItemProperty -LiteralPath $k -Name '(default)' -Value '{00000000-0000-0000-0000-000000000000}'
            }
            Say '已暂时断开 ProgID → CLSID 映射'
            $proc2 = Start-Excel $ReadOnlyWorkbook
            $winC = Get-WindowByTitle '.'
            $mark = Get-LogMark

            # 点击放到后台作业：回调会卡在模态框里不返回，前台要腾出手来抓框。
            $job = Start-Job -ScriptBlock {
                param($ProcId)
                Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
                $AE = [System.Windows.Automation.AutomationElement]
                $TS = [System.Windows.Automation.TreeScope]
                $CT = [System.Windows.Automation.ControlType]
                $w = $AE::RootElement.FindFirst($TS::Children,
                    (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$ProcId)))
                $n = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'ChatSheet')
                $t = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::TabItem)
                $tab = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition($n, $t)))
                if ($tab) { try { $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {} }
                Start-Sleep -Milliseconds 600
                $b = $w.FindFirst($TS::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'ChatSheet 面板')))
                try { $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch {}
            } -ArgumentList $proc2.Id

            # 100ms 粒度：桌面上若有自动点掉对话框的 GUI 代理，框可能只活一秒多。
            $cap = $null
            for ($i = 0; $i -lt 150; $i++) {
                Start-Sleep -Milliseconds 100
                $hits = [PaneVerifyWin32]::FindTop('打不开')
                if ($hits.Count -gt 0) {
                    $h = $hits[0]
                    $cap = @{ Title = [PaneVerifyWin32]::Text($h)
                              Cls = [PaneVerifyWin32]::Cls($h)
                              Children = [PaneVerifyWin32]::Children($h) }
                    Say "第 $([int]($i*100)) 毫秒抓到提示框"
                    break
                }
            }
            Get-Job | Remove-Job -Force -ErrorAction SilentlyContinue

            Chk '弹出了提示框' ($null -ne $cap) '轮询 15 秒未见'
            if ($cap) {
                $bodyParts = @(); $buttons = @()
                foreach ($c in $cap.Children) {
                    $bits = $c.Split([char]1)
                    $cls = $bits[0]
                    $txt = if ($bits.Length -gt 1) { $bits[1] } else { '' }
                    if ($cls -eq 'Static' -and $txt) { $bodyParts += $txt }
                    if ($cls -eq 'Button' -and $txt) { $buttons += $txt }
                }
                $body = $bodyParts -join ' '
                Write-Host '  --- 提示正文 ---' -ForegroundColor Cyan
                foreach ($b in $bodyParts) { Write-Host "  $b" }
                Chk '标题是「ChatSheet 面板打不开」' ($cap.Title -eq 'ChatSheet 面板打不开') $cap.Title
                Chk '是标准对话框 #32770' ($cap.Cls -eq '#32770') $cap.Cls
                Chk '正文说明成因' ($body -match '注册不完整') ''
                Chk '正文给出动作' ($body -match 'install\.bat') ''
                Chk '正文带日志路径' ($body -match '日志：') ''
                Chk '正文不含内部术语「窗格」' (-not ($body -match '窗格')) ''
                Chk '有确定按钮' (($buttons -join '') -match '确定|OK') ($buttons -join '、')
            }
            $log4 = Get-LogSince $mark
            Chk '日志判定成因为 CreateFailed' ($log4 -match '判定成因：CreateFailed') ''
        }
        finally {
            Stop-Excel
            $back = $true
            foreach ($k in $orig.Keys) {
                Set-ItemProperty -LiteralPath $k -Name '(default)' -Value $orig[$k]
                if ((Get-ItemProperty -LiteralPath $k).'(default)' -ne $orig[$k]) { $back = $false }
            }
            Chk '注册已完全还原' $back '注册没还原，请重跑 install.ps1'
        }

        Head '复验：还原后面板恢复正常'
        $proc3 = Start-Excel $ReadOnlyWorkbook
        $winD = Get-WindowByTitle '.'
        Click-PaneButton $winD | Out-Null
        Start-Sleep -Seconds 5
        Chk '还原后面板能正常打开' (Test-PaneVisible $winD) ''
        Chk '还原后不再弹提示' (([PaneVerifyWin32]::FindTop('打不开')).Count -eq 0) ''
    }
}
finally {
    Stop-Excel
    if ($script:madeReadOnly -and (Test-Path -LiteralPath $ReadOnlyWorkbook)) {
        Set-ItemProperty -LiteralPath $ReadOnlyWorkbook -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $ReadOnlyWorkbook -Force -ErrorAction SilentlyContinue
    }
    Head '结果'
    Write-Host ("  通过 $script:pass，失败 $script:fail") -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
}

exit $(if ($script:fail -eq 0) { 0 } else { 1 })
