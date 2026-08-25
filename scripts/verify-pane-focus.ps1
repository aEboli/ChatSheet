<#
.SYNOPSIS
端到端验证：在面板里打过字后点回表格，键盘焦点必须交回 Excel。

.DESCRIPTION
用真实鼠标与键盘输入驱动，判定依据是「Excel UI 线程的焦点窗口」与
「Ctrl+A 之后的实际选区」——只看焦点句柄不足以证明按键真的到了网格。

同时验证不能被改坏的相邻行为：
面板内打字仍进输入框、点编辑栏后按键进编辑栏、点工作表标签能切表。
#>
[CmdletBinding()]
param([switch]$KeepOpen)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Workbook = Join-Path $RepoRoot 'work\p0-test.xlsx'
$LogDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'

if (-not (Test-Path -LiteralPath $Workbook)) { throw "缺少测试工作簿：$Workbook" }

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class XlFocus
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lparam);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hwnd, IntPtr zero);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder t, int m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder t, int m);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte s, uint f, IntPtr e);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint Type; public INPUTUNION Union; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint Msg; public ushort ParamL, ParamH; }
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize; public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    const uint OBJID_NATIVEOM = 0xFFFFFFF0;

    public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    public static string Txt(IntPtr h) { var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); return sb.ToString(); }
    public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }

    public static IntPtr FindTop(int pid, string cls)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { int p; GetWindowThreadProcessId(h, out p);
            if (p == pid && Cls(h) == cls) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindDesc(IntPtr root, string clsPrefix)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(root, (c, l) => {
            if (Cls(c).StartsWith(clsPrefix)) { found = c; return false; } return true; }, IntPtr.Zero);
        return found;
    }

    public static List<string> Desc(IntPtr root)
    {
        var lines = new List<string>();
        EnumChildWindows(root, (c, l) => {
            var r = Rect(c);
            lines.Add(Cls(c) + " #" + c.ToInt64() + " '" + Txt(c) + "' [" +
                r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + "]");
            return true; }, IntPtr.Zero);
        return lines;
    }

    public static IntPtr FocusHwnd(int pid)
    {
        IntPtr main = FindTop(pid, "XLMAIN");
        uint tid = (uint)GetWindowThreadProcessId(main, IntPtr.Zero);
        var info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        GetGUIThreadInfo(tid, ref info);
        return info.hwndFocus;
    }

    public static string FocusName(int pid)
    {
        var h = FocusHwnd(pid);
        return Cls(h) + "#" + h.ToInt64();
    }

    /// <summary>
    /// 每次点击/按键前必须重新确认前台归属的窗口。
    /// 本机有别的应用（聊天软件、任务栏）会间歇抢前台，
    /// 一旦在点击瞬间被抢走，合成输入就落到别处，
    /// 而现象与「焦点没交回」完全一样——那会让验证结论彻底不可信。
    /// </summary>
    public static IntPtr Target;

    /// <summary>
    /// 抢前台。
    /// 系统只允许「当前前台线程」改前台窗口，因此先把本线程的输入队列
    /// 附到当前前台线程上，借它的权限调用 SetForegroundWindow，再解除附着。
    /// 只按 ALT 解锁在有应用持续抢前台时不够用（本机的聊天软件就会）。
    /// </summary>
    public static bool Grab(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return true;

        var fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : (uint)GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint self = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);

        try
        {
            ShowWindow(hwnd, 9);            // SW_RESTORE：目标可能被最小化
            BringWindowToTop(hwnd);
            keybd_event(0x12, 0, 0, IntPtr.Zero);
            keybd_event(0x12, 0, 2, IntPtr.Zero);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(self, fgThread, false);
        }

        System.Threading.Thread.Sleep(200);
        return GetForegroundWindow() == hwnd;
    }

    static void EnsureTarget()
    {
        if (Target == IntPtr.Zero) return;
        for (int i = 0; i < 30; i++)
        {
            if (Grab(Target)) return;
            System.Threading.Thread.Sleep(250);
        }
        throw new InvalidOperationException("目标窗口无法置于前台，合成输入不可信");
    }

    public static void Click(int x, int y)
    {
        EnsureTarget();
        SetCursorPos(x, y); System.Threading.Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(200);
    }

    public static void Key(byte vk)
    {
        EnsureTarget();
        keybd_event(vk, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(40);
        keybd_event(vk, 0, 2, IntPtr.Zero); System.Threading.Thread.Sleep(60);
    }

    public static void CtrlKey(byte vk)
    {
        EnsureTarget();
        keybd_event(0x11, 0, 0, IntPtr.Zero);
        keybd_event(vk, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(40);
        keybd_event(vk, 0, 2, IntPtr.Zero);
        keybd_event(0x11, 0, 2, IntPtr.Zero); System.Threading.Thread.Sleep(120);
    }

    /// <summary>
    /// 用 KEYEVENTF_UNICODE 送字符。
    /// 不能用 keybd_event 送 VK 码：Chromium 走的是自己的输入管线，
    /// 裸 VK 码不带扫描码时不会被当作文本输入，输入框会一个字都收不到——
    /// 那会让「面板仍能打字」这一项在实际坏掉时也显示通过。
    /// </summary>
    public static void Type(string text)
    {
        EnsureTarget();
        foreach (var ch in text)
        {
            var down = new INPUT { Type = 1 };
            down.Union.Keyboard = new KEYBDINPUT { Vk = 0, Scan = ch, Flags = 0x0004, Time = 0, ExtraInfo = IntPtr.Zero };
            var up = new INPUT { Type = 1 };
            up.Union.Keyboard = new KEYBDINPUT { Vk = 0, Scan = ch, Flags = 0x0004 | 0x0002, Time = 0, ExtraInfo = IntPtr.Zero };
            SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(INPUT)));
            System.Threading.Thread.Sleep(50);
        }
        System.Threading.Thread.Sleep(200);
    }

    /// 焦点稳定后再判定：点击后宿主可能还要几十毫秒才把焦点安顿好。
    public static string SettledFocus(int pid)
    {
        string last = FocusName(pid);
        int streak = 0;
        for (int i = 0; i < 20; i++)
        {
            System.Threading.Thread.Sleep(100);
            var now = FocusName(pid);
            if (now == last) { if (++streak >= 3) return now; }
            else { streak = 0; last = now; }
        }
        return last;
    }

    /// SetForegroundWindow 常被系统拒绝；先按一次 ALT 可解锁。
    /// 本机存在其他应用间歇抢前台，故要求前台连续稳定多次。
    public static bool StableForeground(IntPtr hwnd, int needed)
    {
        int streak = 0;
        for (int i = 0; i < 60; i++)
        {
            if (GetForegroundWindow() == hwnd)
            {
                if (++streak >= needed) return true;
            }
            else
            {
                streak = 0;
                Grab(hwnd);
            }
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }

    public static object GetApplication(int pid)
    {
        object result = null;
        IntPtr main = FindTop(pid, "XLMAIN");
        if (main == IntPtr.Zero) return null;
        EnumChildWindows(main, (child, l) => {
            if (Cls(child) != "EXCEL7") return true;
            var iid = new Guid("00020400-0000-0000-C000-000000000046");
            object w;
            if (AccessibleObjectFromWindow(child, OBJID_NATIVEOM, ref iid, out w) == 0 && w != null)
            {
                result = w.GetType().InvokeMember("Application",
                    System.Reflection.BindingFlags.GetProperty, null, w, null);
                return false;
            }
            return true; }, IntPtr.Zero);
        return result;
    }
}
'@

function Write-Step { param([string]$T) Write-Host "==> $T" -ForegroundColor Cyan }
function Write-Ok { param([string]$T) Write-Host "    [通过] $T" -ForegroundColor Green }
function Write-Bad { param([string]$T) Write-Host "    [失败] $T" -ForegroundColor Red }
function Write-Note { param([string]$T) Write-Host "    $T" -ForegroundColor Yellow }

$script:Failures = 0
function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Ok $Message } else { Write-Bad $Message; $script:Failures++ }
}

Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (Test-Path -LiteralPath $LogDir) { Remove-Item -LiteralPath $LogDir -Recurse -Force }

Write-Step '启动 Excel 并打开面板'
Start-Process -FilePath 'C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE' -ArgumentList "`"$Workbook`"" | Out-Null

$app = $null; $xlPid = 0
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    $proc = Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { continue }
    $xlPid = $proc.Id
    try { $app = [XlFocus]::GetApplication($xlPid); if ($app) { break } } catch { }
}
if (-not $app) { throw '连不上 Excel' }

$automation = $null
for ($i = 0; $i -lt 20; $i++) {
    try { $a = $app.COMAddIns.Item('ChatSheet.AddIn'); if ($a -and $a.Object) { $automation = $a.Object; break } } catch { }
    Start-Sleep -Seconds 1
}
if (-not $automation) { throw '取不到加载项自动化接口' }
$automation.ShowPane('chat')
Start-Sleep -Seconds 7

$main = [XlFocus]::FindTop($xlPid, 'XLMAIN')
# 之后每次点击/按键前都会重新确认前台，这里只需先拿到一次。
[XlFocus]::Target = $main
if (-not [XlFocus]::StableForeground($main, 3)) {
    throw '无法把 Excel 置于前台，合成点击不可信，终止。'
}
Write-Note "Excel 已在前台"

$grid = [XlFocus]::FindDesc($main, 'EXCEL7')
$gridRect = [XlFocus]::Rect($grid)
$ocx = [XlFocus]::FindDesc($main, 'CMMOcxHost')
$paneRect = [XlFocus]::Rect($ocx)
Write-Note "网格 [$($gridRect.Left),$($gridRect.Top),$($gridRect.Right),$($gridRect.Bottom)]  面板 [$($paneRect.Left),$($paneRect.Top),$($paneRect.Right),$($paneRect.Bottom)]"

$composerX = [int](($paneRect.Left + $paneRect.Right) / 2)
$composerY = $paneRect.Bottom - 90
$cellX = $gridRect.Left + 220
$cellY = $gridRect.Top + 140

function Get-Selection {
    try { return $app.Selection.Address() } catch { return "<读取失败：$($_.Exception.Message)>" }
}

# ---- 1) 主场景：面板打字后点单元格，Ctrl+A 应全选工作表 ----
Write-Step '主场景：面板打字 → 点单元格 → Ctrl+A'
[XlFocus]::Click($composerX, $composerY)
Start-Sleep -Milliseconds 500
Write-Note "点输入框后焦点 = $([XlFocus]::SettledFocus($xlPid))"
[XlFocus]::Type('hello')
Start-Sleep -Milliseconds 400

[XlFocus]::Click($cellX, $cellY)
Start-Sleep -Milliseconds 700
$focusAfterCell = [XlFocus]::SettledFocus($xlPid)
Write-Note "点单元格后焦点 = $focusAfterCell"
Assert-True ($focusAfterCell -like 'EXCEL7#*') "点单元格后焦点交回网格（实际 $focusAfterCell）"

$before = Get-Selection
[XlFocus]::CtrlKey(0x41)
Start-Sleep -Milliseconds 600
$after = Get-Selection
Write-Note "Ctrl+A：$before → $after"
Assert-True ($after -eq '$1:$1048576') "Ctrl+A 全选工作表（实际 $after）"

# ---- 2) 点同一个已选中的单元格，也必须能交回焦点 ----
# SheetSelectionChange 之类的事件在这种点击下不触发，这一项专门覆盖该缺口。
Write-Step '边界：点回面板后再点「已选中的同一单元格」'
[XlFocus]::Click($cellX, $cellY)
Start-Sleep -Milliseconds 400
$cellNow = Get-Selection
[XlFocus]::Click($composerX, $composerY)
Start-Sleep -Milliseconds 500
Write-Note "回到输入框，焦点 = $([XlFocus]::SettledFocus($xlPid))"
[XlFocus]::Click($cellX, $cellY)   # 点的还是同一个单元格，选区不变
Start-Sleep -Milliseconds 700
$focusSame = [XlFocus]::SettledFocus($xlPid)
Assert-True ($focusSame -like 'EXCEL7#*') "点同一单元格后焦点交回网格（实际 $focusSame）"
[XlFocus]::CtrlKey(0x41)
Start-Sleep -Milliseconds 600
$afterSame = Get-Selection
Assert-True ($afterSame -eq '$1:$1048576') "同一单元格场景 Ctrl+A 仍全选（选区 $cellNow → $afterSame）"

# ---- 3) 面板内打字与面板内的 Ctrl+A 不能被守卫破坏 ----
# 必须真的读回文字：只看焦点句柄的话，输入管线坏掉时这项仍会显示通过。
Write-Step '不能改坏：面板内输入仍进输入框，面板内 Ctrl+A 仍选中输入框文字'
[XlFocus]::Click($composerX, $composerY)
Start-Sleep -Milliseconds 500
[XlFocus]::Type('chatsheet')
Start-Sleep -Milliseconds 600
$focusInPane = [XlFocus]::SettledFocus($xlPid)
Assert-True ($focusInPane -like 'Chrome_*') "面板内打字时焦点留在面板（实际 $focusInPane）"

# 输入框内容跨步骤累加（第 1 步打过 hello），因此只断言「刚打的字到了末尾」。
$composerState = $automation.ReadComposerTextForTest()
Write-Note "输入框读回：'$composerState'"
$composerValue = ($composerState -split '\|')[0]
Assert-True ($composerValue.EndsWith('chatsheet')) "键入的字进了输入框（实际 '$composerValue'）"

# 面板内按 Ctrl+A：应选中输入框全文，且不得改动工作表选区。
$sheetBefore = Get-Selection
[XlFocus]::CtrlKey(0x41)
Start-Sleep -Milliseconds 500
$afterCtrlA = $automation.ReadComposerTextForTest()
$sheetAfter = Get-Selection
Write-Note "面板内 Ctrl+A 后：输入框 '$afterCtrlA'，工作表选区 $sheetBefore → $sheetAfter"
$range = ($afterCtrlA -split '\|')[1]
Assert-True ($range -eq "0-$($composerValue.Length)") "面板内 Ctrl+A 选中输入框全文（期望 0-$($composerValue.Length)，实际 $range）"
Assert-True ($sheetAfter -eq $sheetBefore) "面板内 Ctrl+A 不改动工作表选区（$sheetBefore → $sheetAfter）"

# ---- 4) 编辑栏：点它之后按键应进编辑栏，不应被抢到网格 ----
Write-Step '不能改坏：点编辑栏后焦点归编辑栏'
$formulaBar = $null
foreach ($line in [XlFocus]::Desc($main)) {
    if ($line -match '^EXCEL<[^>]*> #(\d+).*\[(\d+),(\d+),(\d+),(\d+)\]') { }
}
# 编辑栏窗口类名在不同版本不稳定，改用坐标：位于网格上方、面板左侧的窄条。
$fbY = $gridRect.Top - 22
$fbX = $gridRect.Left + 300
Write-Note "点击编辑栏位置 ($fbX, $fbY)，该处窗口 = $([XlFocus]::Cls([XlFocus]::FocusHwnd($xlPid)))"
[XlFocus]::Click($composerX, $composerY)   # 先让焦点回到面板
Start-Sleep -Milliseconds 400
[XlFocus]::Click($fbX, $fbY)
Start-Sleep -Milliseconds 700
$focusFb = [XlFocus]::SettledFocus($xlPid)
Write-Note "点编辑栏后焦点 = $focusFb"
Assert-True ($focusFb -notlike 'Chrome_*') "点编辑栏后焦点离开面板（实际 $focusFb）"

# ---- 5) 工作表标签：点它之后应能用键盘继续操作网格 ----
Write-Step '不能改坏：点工作表标签后仍可操作'
[XlFocus]::Click($composerX, $composerY)
Start-Sleep -Milliseconds 400
$tabX = $gridRect.Left + 40
$tabY = $gridRect.Bottom + 14
[XlFocus]::Click($tabX, $tabY)
Start-Sleep -Milliseconds 700
$focusTab = [XlFocus]::SettledFocus($xlPid)
Write-Note "点工作表标签后焦点 = $focusTab"
Assert-True ($focusTab -notlike 'Chrome_*') "点工作表标签后焦点离开面板（实际 $focusTab）"

# ---- 6) 功能区：守卫会把焦点交给被点的窗口，而功能区平时并不接管焦点，
# 这是本改动唯一可能偏离宿主原有行为的地方，必须与基线对照，不能只看绝对结果。 ----
Write-Step '不能改坏：点功能区后方向键的行为与基线一致'
# 点选项卡区而不是按钮区：按钮会真的执行命令，可能改动工作簿。
$ribbonX = $gridRect.Left + 260
$ribbonY = 55

# 只比较「方向键是否移动了选区」这一行为，不比较绝对地址：
# 两次测量的起点本来就可能不同，比地址会把状态差异误报成回归。
function Measure-ArrowAfterRibbon {
    [XlFocus]::Key(0x1B)                      # Esc：清掉可能残留的功能区导航态
    Start-Sleep -Milliseconds 300
    [XlFocus]::Click($cellX, $cellY)          # 选区放到已知位置
    Start-Sleep -Milliseconds 400
    $start = Get-Selection
    [XlFocus]::Click($ribbonX, $ribbonY)      # 点功能区选项卡
    Start-Sleep -Milliseconds 700
    [XlFocus]::Key(0x28)                      # 方向键下
    Start-Sleep -Milliseconds 500
    $end = Get-Selection
    return [pscustomobject]@{ Start = $start; End = $end; Moved = ($end -ne $start) }
}

# 基线：焦点本就不在面板时的表现。
$baseline = Measure-ArrowAfterRibbon
Write-Note "基线（焦点不在面板）：$($baseline.Start) → $($baseline.End)，移动=$($baseline.Moved)"

# 对照：先让焦点进面板，再走同一串操作。
[XlFocus]::Click($composerX, $composerY)
Start-Sleep -Milliseconds 500
$withPane = Measure-ArrowAfterRibbon
Write-Note "对照（焦点先在面板）：$($withPane.Start) → $($withPane.End)，移动=$($withPane.Moved)"
Assert-True ($withPane.Moved -eq $baseline.Moved) `
    "点功能区后方向键行为与基线一致（基线移动=$($baseline.Moved)，对照移动=$($withPane.Moved)）"

Write-Step '加载项日志'
if (Test-Path -LiteralPath $LogDir) {
    foreach ($log in @(Get-ChildItem -LiteralPath $LogDir -Filter '*.log')) {
        Get-Content -LiteralPath $log.FullName -Encoding UTF8 |
            Where-Object { $_ -match '守卫|ERROR|WARN' } |
            ForEach-Object { Write-Host "    $_" }
    }
}

Write-Host ''
if ($script:Failures -eq 0) {
    Write-Host "全部通过。" -ForegroundColor Green
} else {
    Write-Host "$($script:Failures) 项失败。" -ForegroundColor Red
}

if (-not $KeepOpen) {
    Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Note '已关闭 Excel'
}

if ($script:Failures -ne 0) { exit 1 }
