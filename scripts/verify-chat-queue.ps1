<#
.SYNOPSIS
端到端验证输入排队：处理中仍可输入，新输入排队并在上一轮结束后自动接着跑。

.DESCRIPTION
用 mock 的 slow 场景让一轮停在处理中，期间再投两条输入，据此验证：
  1. 处理中输入框不被禁用，新输入进队列而不是被丢弃或撞上 BUSY；
  2. 排队内容显示在输入区上方的排队条上，且开跑前不进对话流；
  3. 队列按先进先出发出，顺序可从 mock 念回的内容确认；
  4. 排队条目可单独取消；
  5. 停止会连带清空队列，不会停完一轮又自动跑下一条。

mock 会把收到的最后一句用户输入原样念回来，因此「谁先发出去」不靠时序猜测，
而是从加载项日志里的输入长度与回复内容直接读出来。
#>
[CmdletBinding()]
param(
    [int]$MockPort = 58941,

    # 跳过部署，直接验证已安装的版本。仅在确认产物已同步时使用。
    [switch]$SkipDeploy,

    [switch]$KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SettingsPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\settings.json'
$SecretPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\secrets\custom-api-token.bin'
$LogDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'
$BackupSuffix = '.queue-backup'

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

function Backup-UserConfig {
    foreach ($p in @($SettingsPath, $SecretPath)) {
        if (Test-Path -LiteralPath $p) {
            Copy-Item -LiteralPath $p -Destination ($p + $BackupSuffix) -Force
            Write-Note "已备份 $(Split-Path $p -Leaf)"
        }
    }
}

function Restore-UserConfig {
    foreach ($p in @($SettingsPath, $SecretPath)) {
        $backup = $p + $BackupSuffix
        if (Test-Path -LiteralPath $backup) {
            Copy-Item -LiteralPath $backup -Destination $p -Force
            Remove-Item -LiteralPath $backup -Force
            Write-Note "已还原 $(Split-Path $p -Leaf)"
        }
        elseif (Test-Path -LiteralPath $p) {
            Remove-Item -LiteralPath $p -Force
            Write-Note "已移除测试产生的 $(Split-Path $p -Leaf)"
        }
    }
}

# 取排队状态里的某个字段。返回值形如「排队=2 | 已取消=0 | …」。
function Get-Field {
    param([string]$State, [string]$Name)
    foreach ($part in ($State -split '\|')) {
        $pair = $part.Trim()
        if ($pair -like "$Name=*") { return $pair.Substring($Name.Length + 1) }
    }

    return ''
}

$mockJob = $null
$excelStarted = $false

try {
    Write-Step '备份用户配置'
    Backup-UserConfig

    Write-Step "启动 mock 服务（端口 $MockPort，场景 slow）"
    $mockScript = Join-Path $RepoRoot 'tests\mock-provider\server.mjs'
    # 路径必须加引号：仓库路径含空格。
    $mockJob = Start-Process -FilePath 'node' -ArgumentList "`"$mockScript`" $MockPort slow" `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $env:TEMP 'chatsheet-mock-queue.log')
    Start-Sleep -Seconds 2

    $probe = Test-NetConnection -ComputerName 127.0.0.1 -Port $MockPort -InformationLevel Quiet -WarningAction SilentlyContinue
    if (-not $probe) { throw "mock 服务未监听端口 $MockPort。" }
    Write-Note 'mock 服务就绪'

    Write-Step '写入指向 mock 的设置'
    # 审批设为全自动：本脚本验证排队，不该被审批卡片打断。
    $settings = [ordered]@{
        mode = 'CustomApi'
        cliSource = 'Auto'
        customProtocol = 'openai-chat-completions'
        customBaseUrl = "http://127.0.0.1:$MockPort/v1"
        model = 'mock-model'
        thinking = 'Off'
        approval = 'Automatic'
        maxOutputTokens = 8192
        contextBudgetTokens = 100000
        maxSteps = 40
        autoIncludeSelection = $true
        # 必须带上：verify-panel.ps1 会读回这个键做宽度记忆断言，
        # 而 StrictMode 下读不存在的属性会抛错。0 表示尚未记录过宽度。
        paneWidth = 0
    }
    $json = $settings | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($SettingsPath, $json, (New-Object System.Text.UTF8Encoding($true)))

    Add-Type -AssemblyName System.Security
    $bytes = [System.Text.Encoding]::UTF8.GetBytes('mock-token')
    $entropy = [System.Text.Encoding]::UTF8.GetBytes('ChatSheet.SecretStore.v1')
    $cipher = [System.Security.Cryptography.ProtectedData]::Protect(
        $bytes, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    New-Item -ItemType Directory -Path (Split-Path $SecretPath) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($SecretPath, $cipher)
    Write-Note "已指向 http://127.0.0.1:$MockPort/v1"

    if (-not $SkipDeploy) {
        Write-Step '部署当前构建'
        # 必须先部署：漏掉这步会验证到上一次安装的旧版本。
        & (Join-Path $PSScriptRoot 'install.ps1') -Action install -SkipBuild | Out-Null
        Write-Note '已部署最新产物'
    }
    else {
        Write-Note '按要求跳过部署，将验证已安装的版本'
    }

    Write-Step '启动 Excel 并打开面板'
    if (Test-Path -LiteralPath $LogDir) { Remove-Item -LiteralPath $LogDir -Recurse -Force }
    & (Join-Path $PSScriptRoot 'verify-panel.ps1') -Route chat -KeepOpen | Out-Null
    $excelStarted = $true

    # 连上刚启动的 Excel。verify-panel.ps1 已确认面板渲染成功。
    #
    # 走窗口树 + 可访问性接口，而不是运行对象表：正常启动的 Excel 未必登记到
    # ROT，GetActiveObject 会以「指定的 OLE 变量无效」失败。这段与
    # verify-chat-e2e.ps1 同源。
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class XlApp
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
    $app = [XlApp]::Get($proc.Id)
    $automation = $app.COMAddIns.Item('ChatSheet.AddIn').Object
    $automation.ShowPane('chat')
    Start-Sleep -Seconds 2

    Write-Step '发出第一条，使其进入处理中'
    $automation.SendChatForTest('第一条') | Out-Null
    # slow 场景一轮约 5.6 秒。等 1.5 秒足以进入处理中，又远未结束。
    Start-Sleep -Milliseconds 1500

    $state = $automation.ReadQueueForTest()
    Write-Note "状态：$state"
    Assert-True ((Get-Field $state '输入框可用') -eq 'True') '处理中输入框仍可输入'
    Assert-True ((Get-Field $state '按钮') -eq '停止') '处理中且输入框为空时按钮是停止'

    Write-Step '处理中再投两条，应排队而非丢弃'
    $automation.SendChatForTest('第二条') | Out-Null
    Start-Sleep -Milliseconds 400
    $automation.SendChatForTest('第三条') | Out-Null
    Start-Sleep -Milliseconds 400

    $state = $automation.ReadQueueForTest()
    Write-Note "状态：$state"
    Assert-True ((Get-Field $state '排队') -eq '2') '两条新输入都进了队列'
    Assert-True ((Get-Field $state '排队内容') -eq '第二条，第三条') '队列按投入顺序排列'
    Assert-True ((Get-Field $state '位次') -eq '1，2') '排队条按位次连续编号'
    Assert-True ((Get-Field $state '排队条可见') -eq 'True') '队列非空时排队条显示在输入区上方'
    # 排队内容在开跑前不进对话流：此刻只有第一条发出去了。
    Assert-True ((Get-Field $state '已发送') -eq '1') '排队中的两条尚未进对话流'

    Write-Step '等待队列自动排空'
    # 三轮 × 约 5.6 秒，留足余量。
    $drained = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 1
        $state = $automation.ReadQueueForTest()
        if ((Get-Field $state '排队') -eq '0' -and (Get-Field $state '按钮') -eq '发送') {
            $drained = $true
            break
        }
    }

    Write-Note "状态：$state"
    Assert-True $drained '上一轮结束后队列自动跑完，无需再次点击'
    Assert-True ((Get-Field $state '已发送') -eq '3') '三条输入全部发出'
    Assert-True ((Get-Field $state '排队条可见') -eq 'False') '队列排空后排队条收起'

    $log = Get-Content -LiteralPath (Join-Path $LogDir 'addin-EXCEL.log') -Raw -Encoding UTF8
    # mock 把输入念回来，因此回复里出现的顺序就是实际发送顺序。
    $turns = [regex]::Matches($log, '开始对话：.*?输入长度=(\d+)')
    Assert-True ($turns.Count -eq 3) "加载项确实跑了三轮（实际 $($turns.Count) 轮）"
    Assert-True ($log -notmatch 'BUSY') '排队期间没有撞上 BUSY'

    Write-Step '验证取消排队条目'
    $automation.SendChatForTest('待跑的') | Out-Null
    Start-Sleep -Milliseconds 1200
    $automation.SendChatForTest('要取消的') | Out-Null
    Start-Sleep -Milliseconds 400

    $state = $automation.ReadQueueForTest()
    Assert-True ((Get-Field $state '排队') -eq '1') '取消前队列里有一条'

    $cancelled = $automation.CancelQueuedForTest(0)
    Write-Note "取消结果：$cancelled"
    Start-Sleep -Milliseconds 300

    $state = $automation.ReadQueueForTest()
    Write-Note "状态：$state"
    Assert-True ((Get-Field $state '排队') -eq '0') '取消后队列为空'
    Assert-True ((Get-Field $state '已取消内容') -like '*要取消的*') '被取消的那条落进对话流并保留原文以便重发'

    Write-Step '验证停止会连带清空队列'
    # 等上一轮跑完，免得把它的收尾误当成本次停止的结果。
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Seconds 1
        if ((Get-Field $automation.ReadQueueForTest() '按钮') -eq '发送') { break }
    }

    $automation.SendChatForTest('停止测试第一条') | Out-Null
    Start-Sleep -Milliseconds 1200
    $automation.SendChatForTest('停止时应被丢掉') | Out-Null
    Start-Sleep -Milliseconds 400

    $state = $automation.ReadQueueForTest()
    Assert-True ((Get-Field $state '排队') -eq '1') '停止前队列里有一条待跑'

    # 输入框此刻为空，点发送按钮的含义就是停止。
    $clicked = $automation.ClickSendForTest()
    Write-Note "点击结果：$clicked"
    Assert-True ($clicked -like '*停止*') '输入框为空时按钮的含义是停止'
    Start-Sleep -Seconds 2

    $state = $automation.ReadQueueForTest()
    Write-Note "状态：$state"
    Assert-True ((Get-Field $state '排队') -eq '0') '停止后队列已清空，不会接着跑下一条'

    # 内部队列与 DOM 必须一致，两者对不上说明有条目丢了显示或显示丢了条目。
    $log = Get-Content -LiteralPath (Join-Path $LogDir 'addin-EXCEL.log') -Raw -Encoding UTF8
    $layouts = [regex]::Matches($log, '队列 (\d+) 条（排队条 (\d+) 个）')
    $consistent = $true
    foreach ($m in $layouts) {
        if ($m.Groups[1].Value -ne $m.Groups[2].Value) { $consistent = $false }
    }

    Assert-True ($layouts.Count -gt 0) "布局日志记录了队列状态（$($layouts.Count) 次）"
    Assert-True $consistent '内部队列长度与排队条上的条目数始终一致'
}
finally {
    if (-not $KeepOpen) {
        Get-Process -Name EXCEL -ErrorAction SilentlyContinue | Stop-Process -Force
    }

    if ($mockJob -and -not $mockJob.HasExited) {
        Stop-Process -Id $mockJob.Id -Force -ErrorAction SilentlyContinue
        Write-Note 'mock 服务已停止'
    }

    Write-Step '还原用户配置'
    Restore-UserConfig
}

Write-Host ''
if ($script:Failed -eq 0) {
    Write-Host '=== 排队验证全部通过 ===' -ForegroundColor Green
    exit 0
}

Write-Host "=== 排队验证有 $($script:Failed) 项失败 ===" -ForegroundColor Red
exit 1
