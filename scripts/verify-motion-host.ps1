<#
.SYNOPSIS
在真实 Excel 里验证对话界面的动效，用的是已安装的那份构建。

.DESCRIPTION
与 verify-motion.ps1 的分工：那个跑静态核对与 PaneHarness（自带宿主，快），
这个证明「用户那边真的生效」——Excel 加载的是安装目录里的 DLL 与 web 文件，
源码修好、PaneHarness 全绿，都不等于装上的那份是新的。

验的是四件只有真实渲染器算得出来的事：进场动画确实在跑、重挂不把它倒回重播、
动画被取消后类摘得掉、顶栏三个图标各放对应的关键帧且都在绑定范围内。

必须正常启动 Excel 并带文档：用 COM 自动化启动的实例会跳过 COM 加载项。
#>
[CmdletBinding()]
param(
    [switch]$KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Workbook = Join-Path $RepoRoot 'work\p0-test.xlsx'

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
    Write-Bad $(if ($Detail) { "$Label：$Detail" } else { $Label })
}

<# 从 `a=1 | b=2` 里取一个字段。 #>
function Get-Field {
    param([string]$Text, [string]$Name)

    foreach ($part in ($Text -split '\|')) {
        $pair = $part.Trim()
        if ($pair.StartsWith("$Name=")) { return $pair.Substring($Name.Length + 1).Trim() }
    }
    return ''
}

<# 从 `名字@毫秒` 取毫秒数。取不到返回 -1。 #>
function Get-AnimTime {
    param([string]$Value)

    $at = $Value.IndexOf('@')
    if ($at -lt 0) { return -1 }
    $rest = $Value.Substring($at + 1)
    $plus = $rest.IndexOf('+')
    if ($plus -ge 0) { $rest = $rest.Substring(0, $plus) }
    $parsed = 0
    if ([int]::TryParse($rest.Trim(), [ref]$parsed)) { return $parsed }
    return -1
}

if (-not (Test-Path -LiteralPath $Workbook)) { throw "缺少测试工作簿：$Workbook" }

Write-Step '核对安装目录里的那份是新的'
# 这一步不能省。Excel 跑的是安装目录里的文件，源码改好不等于用户那边改好。
$installed = Join-Path $env:LOCALAPPDATA 'ChatSheet\app'
foreach ($rel in @('web\scripts\motion.js', 'web\scripts\chat.js', 'web\styles\app.css')) {
    $path = Join-Path $installed $rel
    Assert "已安装 $rel" (Test-Path -LiteralPath $path) $path
}
if (Test-Path -LiteralPath (Join-Path $installed 'web\scripts\chat.js')) {
    $chat = Get-Content -LiteralPath (Join-Path $installed 'web\scripts\chat.js') -Raw -Encoding UTF8
    Assert '安装的 chat.js 带重挂修复与减动效判断' `
        ($chat -match 'animationcancel' -and $chat -match 'prefersReducedMotion') `
        '装的是旧构建，先跑 scripts\install.ps1'
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

Write-Step '启动 Excel'
$exe = 'C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE'
if (-not (Test-Path -LiteralPath $exe)) { throw "未找到 Excel：$exe" }
Start-Process -FilePath $exe -ArgumentList "`"$Workbook`"" | Out-Null

# 经可访问性接口取 Application，不走运行对象表：提权终端与 Excel 的完整性
# 级别不一致时 GetActiveObject 根本取不到。
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class XlMotion
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

try {
    Write-Step '连接 Excel 与加载项'
    $app = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $proc = Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc) { continue }
        try { $app = [XlMotion]::Get($proc.Id); if ($app) { break } } catch { }
    }
    if (-not $app) { throw '无法取得 Excel Application 对象。' }
    Write-Ok "已连接 Excel $($app.Version)"

    $auto = $null
    for ($i = 0; $i -lt 20; $i++) {
        try {
            $addin = $app.COMAddIns.Item('ChatSheet.AddIn')
            if ($addin -and $addin.Object) { $auto = $addin.Object; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $auto) { throw '取不到加载项自动化接口，加载项可能未成功加载。' }
    Write-Ok '已取得自动化接口'

    Write-Step '打开面板'
    $auto.ShowPane('chat')
    Start-Sleep -Seconds 6
    Assert '面板可见' ([bool]$auto.IsPaneVisible)

    Write-Step '进场动画'
    $auto.DriveMotionForTest('reset') | Out-Null
    $mounted = $auto.DriveMotionForTest('mount')
    Write-Ok "首挂：$mounted"
    Assert '首次挂载挂上了进场类' ($mounted -match 'is-entering') $mounted
    Assert '进场动画真的在跑' ((Get-Field $mounted '动画') -like 'transcript-enter@*') $mounted

    # 关键一条：append 一个已在场的节点会把运行中的动画取消并重播，
    # 表现是那个气泡可见地闪两下。
    #
    # 这里断的是不变式，不是进度值：**重挂后的节点绝不能还带着进场类、也绝不能
    # 在动画中**。修好的版本重挂前会主动摘类，于是 append 之后没有任何动画可起播；
    # 没修的版本类还在，append 把动画从头重播，两处都读得出来（实测坏版本是
    # `类=… is-entering`、`动画=transcript-enter@0`）。
    #
    # 为什么不在这里量进度：量「有没有退回去」必须让渲染器出帧（同一个 JS 任务内
    # document.timeline.currentTime 是常量，在页面里忙等推不动它），于是首挂与
    # 重挂只能分两次调用；而经 COM 往返远超 0.18s 的动画时长，重挂时动画早已
    # 自然放完，「重挂前」量到的是「无」——那条断言就只能靠「量不到也算过」
    # 通过，是一次假绿。进度这条判据留在 PaneHarness --motion 里（进程内够快，
    # 实测 130-170ms → 0ms，回退修复会当场变红）。
    # remount-same-task 把首挂与重挂放进同一个 JS 任务，中间不出帧，因此
    # animationend 绝无可能已触发——类此刻在不在，完全取决于代码有没有在重挂时
    # 主动摘掉它。两个版本必然给出不同结果，与 COM 快慢无关。
    $remounted = $auto.DriveMotionForTest('remount-same-task')
    Write-Ok "重挂：$remounted"

    # 前置断言：首挂那一下确实加了类并起播了动画，否则后面断的是空气。
    Assert '首挂确实加了进场类并起播了动画（下面断言的前提）' `
        ((Get-Field $remounted '首挂') -match 'is-entering' -and `
         (Get-Field $remounted '首挂') -match 'transcript-enter@') $remounted
    Assert '重挂的是同一个节点（否则下面断的不是它）' `
        ((Get-Field $remounted '同一节点') -eq 'true') $remounted
    Assert '重挂后节点不再带进场类' `
        ((Get-Field $remounted '重挂后类') -notmatch 'is-entering') $remounted
    Assert '重挂后没有动画在跑（带类重挂会被 append 重播，表现是闪两下）' `
        ((Get-Field $remounted '重挂后动画') -eq '无') $remounted

    Write-Step '动画被取消时类要摘得掉'
    # 用工具卡片：每次推送都新建一张，因此一定是全新首挂、动画确实在跑。
    $card = $auto.DriveMotionForTest('card')
    Write-Ok "新卡：$card"
    Assert '工具卡片首挂时动画在跑（下一条断言的前提）' `
        ((Get-Field $card '动画') -like 'transcript-enter@*') $card

    $moved = $auto.DriveMotionForTest('move-card-away')
    Write-Ok "搬走：$moved"
    Assert '搬走时动画确实还在跑（否则测不到取消）' `
        ((Get-AnimTime (Get-Field $moved '搬前')) -ge 0) $moved

    Start-Sleep -Milliseconds 150
    $settled = $auto.DriveMotionForTest('card-state')
    Write-Ok "搬走后：$settled"
    Assert '动画被取消后进场类摘掉了' ((Get-Field $settled '残留') -eq 'false') $settled

    Write-Step '顶栏图标的点击回弹'
    foreach ($id in @('chat', 'settings', 'theme')) {
        $tapped = $auto.DriveMotionForTest("tap:$id")
        Write-Ok "点 $id：$tapped"
        Assert "$id 在 .app-nav .nav-btn 的绑定范围内" ((Get-Field $tapped '绑定') -eq 'true') $tapped
        Assert "点 $id 之后挂上了回弹类" ($tapped -match 'is-tapped') $tapped

        $expected = if ($id -eq 'theme') { 'theme-tap' } else { 'nav-tap' }
        Assert "$id 放的是 $expected" ((Get-Field $tapped '动画') -like "$expected@*") $tapped
        Start-Sleep -Milliseconds 420
    }

    Write-Step '连点要重新起播'
    $twice = $auto.DriveMotionForTest('tap-twice:chat')
    Write-Ok "连点：$twice"
    $t1 = Get-AnimTime (Get-Field $twice '第一下')
    $t2 = Get-AnimTime (Get-Field $twice '第二下')
    Assert "连点第二下重新起播（${t1}ms → ${t2}ms）" `
        ($t1 -ge 0 -and $t2 -ge 0 -and $t2 -le ($t1 + 5)) `
        '第二下的进度比第一下靠后说明动画没重启，连点没有反馈'

    $auto.DriveMotionForTest('reset') | Out-Null

    Write-Host ''
    $color = if ($failed -eq 0) { 'Green' } else { 'Red' }
    Write-Host "=== 真实 Excel 动效验证：通过 $passed，失败 $failed ===" -ForegroundColor $color
}
finally {
    if (-not $KeepOpen) {
        Get-Process -Name 'EXCEL' -ErrorAction SilentlyContinue | Stop-Process -Force
        Write-Note '已关闭 Excel。加 -KeepOpen 可保留窗口手动查看面板。'
    }
}

exit $(if ($failed -eq 0) { 0 } else { 1 })
