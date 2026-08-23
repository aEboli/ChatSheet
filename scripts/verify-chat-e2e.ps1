<#
.SYNOPSIS
端到端验证对话链路：流式文本、工具调用、工具执行、消息推送。

.DESCRIPTION
用本地 mock 服务替代真实接口，因此不消耗任何额度、不需要真实密钥。
会临时改写设置以指向 mock，结束后自动还原用户原有设置。

验证重点是线程模型：Agent 循环在 await 之后位于线程池线程，
而 WebView2 与宿主 COM 都要求 UI 线程，此前正是这里出错导致「发消息没反应」。
#>
[CmdletBinding()]
param(
    [int]$MockPort = 58940,

    # 默认策略是写操作逐项审批，这是用户实际会遇到的路径，需单独验证。
    [ValidateSet('Automatic', 'PerWrite')]
    [string]$Approval = 'Automatic',

    # 跳过部署，直接验证已安装的版本。仅在确认产物已同步时使用。
    [switch]$SkipDeploy,

    # 上下文预算。下限受 Settings.Normalize 约束为 8000，
    # 配合 bulk 场景可堆到 90% 阈值以验证压缩路径。
    [int]$ContextBudget = 100000,

    # mock 场景：tool 走一次工具调用后收尾；
    # bulk 连续多轮读取以快速堆高上下文；
    # image 只回文本并报出收到的图片数，用于验证多模态链路。
    [ValidateSet('tool', 'bulk', 'image')]
    [string]$Scenario = 'tool',

    # image 场景下附加一张测试图片。
    [switch]$WithImage,

    [switch]$KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SettingsPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\settings.json'
$SecretPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\secrets\custom-api-token.bin'
$LogDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'
$Workbook = Join-Path $RepoRoot 'work\p0-test.xlsx'
$BackupSuffix = '.e2e-backup'

function Write-Step { param([string]$T) Write-Host "==> $T" -ForegroundColor Cyan }
function Write-Ok { param([string]$T) Write-Host "    $T" -ForegroundColor Green }
function Write-Bad { param([string]$T) Write-Host "    $T" -ForegroundColor Red }
function Write-Note { param([string]$T) Write-Host "    $T" -ForegroundColor Yellow }

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
            # 原本不存在的文件，测试期间新建的要删掉。
            Remove-Item -LiteralPath $p -Force
            Write-Note "已移除测试产生的 $(Split-Path $p -Leaf)"
        }
    }
}

$mockJob = $null
$excelStarted = $false

try {
    Write-Step '备份用户配置'
    Backup-UserConfig

    Write-Step "启动 mock 服务（端口 $MockPort）"
    $mockScript = Join-Path $RepoRoot 'tests\mock-provider\server.mjs'
    # 脚本路径必须加引号：仓库路径含空格，否则 node 会把它按空格截断。
    $mockJob = Start-Process -FilePath 'node' -ArgumentList "`"$mockScript`" $MockPort $Scenario" `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $env:TEMP 'chatsheet-mock.log')
    Start-Sleep -Seconds 2

    $probe = Test-NetConnection -ComputerName 127.0.0.1 -Port $MockPort -InformationLevel Quiet -WarningAction SilentlyContinue
    if (-not $probe) { throw "mock 服务未监听端口 $MockPort。" }
    Write-Ok 'mock 服务就绪'

    Write-Step '写入指向 mock 的设置'
    # 直接写设置文件与密钥：绕过界面以便脚本化。
    $settings = [ordered]@{
        mode = 'CustomApi'
        cliSource = 'Auto'
        customProtocol = 'openai-chat-completions'
        customBaseUrl = "http://127.0.0.1:$MockPort/v1"
        model = 'mock-model'
        thinking = 'Off'
        approval = $Approval
        maxOutputTokens = 8192
        contextBudgetTokens = $ContextBudget
        maxSteps = 40
        autoIncludeSelection = $true
    }
    $json = $settings | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($SettingsPath, $json, (New-Object System.Text.UTF8Encoding($true)))

    # mock 不校验密钥，但配置校验要求它存在，用 DPAPI 写一个占位值。
    Add-Type -AssemblyName System.Security
    $bytes = [System.Text.Encoding]::UTF8.GetBytes('mock-token')
    $entropy = [System.Text.Encoding]::UTF8.GetBytes('ChatSheet.SecretStore.v1')
    $cipher = [System.Security.Cryptography.ProtectedData]::Protect(
        $bytes, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    New-Item -ItemType Directory -Path (Split-Path $SecretPath) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($SecretPath, $cipher)
    Write-Ok "已指向 http://127.0.0.1:$MockPort/v1，模型 mock-model"

    Write-Step '部署当前构建'
    # 必须先部署：本脚本只负责启动宿主，不会自动同步产物。
    # 漏掉这步会验证到上一次安装的旧版本，得出与代码不符的结论。
    if (-not $SkipDeploy) {
        & (Join-Path $PSScriptRoot 'install.ps1') -Action install -SkipBuild | Out-Null
        Write-Ok '已部署最新产物'
    }
    else {
        Write-Note '按要求跳过部署，将验证已安装的版本'
    }

    Write-Step '启动 Excel 并打开面板'
    if (Test-Path -LiteralPath $LogDir) { Remove-Item -LiteralPath $LogDir -Recurse -Force }
    & (Join-Path $PSScriptRoot 'verify-panel.ps1') -Route chat -KeepOpen | Out-Null
    $excelStarted = $true

    Write-Step '通过面板发送消息'
    # 用 WebView2 执行脚本模拟真实点击：直接填入输入框并触发发送，
    # 走的是与用户操作完全相同的代码路径。
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
'@

    $proc = Get-Process -Name EXCEL | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    $app = [XlApp]::Get($proc.Id)
    $automation = $app.COMAddIns.Item('ChatSheet.AddIn').Object
    $automation.ShowPane('chat')
    Start-Sleep -Seconds 2

    if ($WithImage) {
        Write-Step '附加测试图片'
        # 1×1 像素的合法 PNG，来自官方文档示例。
        $tinyPng = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC'
        $attached = $automation.AttachImageForTest($tinyPng, 'tiny.png')
        Write-Ok "附加结果：$attached"
        if ($attached -notmatch '已附加') {
            Write-Bad '图片未成功附加'
        }
    }

    $sent = $automation.SendChatForTest('把表头写成名称和数量')
    Write-Ok "已投递测试消息：$sent"

    $logFile = Join-Path $LogDir 'addin-EXCEL.log'

    if ($Approval -eq 'PerWrite') {
        Write-Step '等待审批卡片并点击「允许」'
        # 逐项审批策略下，Agent 会挂起等待用户决定。
        # 这里用脚本点击真实按钮，走与手工操作相同的路径。
        $approved = $false
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            $clicked = $automation.ClickApprovalForTest($true)
            if ($clicked -match '已点击') {
                Write-Ok $clicked
                $approved = $true
                break
            }
        }

        if (-not $approved) { Write-Bad '未出现审批卡片' }
    }

    Write-Step '等待对话完成（最多 60 秒）'
    $deadline = (Get-Date).AddSeconds(60)
    $done = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if (-not (Test-Path -LiteralPath $logFile)) { continue }
        $content = Get-Content -LiteralPath $logFile -Encoding UTF8 -Raw
        if ($content -match '对话结束') { $done = $true; break }
    }

    if (-not $done) { Write-Note '未在时限内见到对话结束记录' }

    Write-Step '日志'
    if (Test-Path -LiteralPath $logFile) {
        Get-Content -LiteralPath $logFile -Encoding UTF8 | ForEach-Object { Write-Host "      $_" }
    }

    Write-Step 'mock 服务日志'
    $mockLog = Join-Path $env:TEMP 'chatsheet-mock.log'
    if (Test-Path -LiteralPath $mockLog) {
        Get-Content -LiteralPath $mockLog | ForEach-Object { Write-Host "      $_" }
    }

    if ($Scenario -eq 'image') {
        Write-Step '图片链路判定'
        $mockLogPath = Join-Path $env:TEMP 'chatsheet-mock.log'
        $mockText = if (Test-Path -LiteralPath $mockLogPath) {
            Get-Content -LiteralPath $mockLogPath -Raw
        } else { '' }

        $imageMatch = [regex]::Match($mockText, 'images=(\d+)(?: types=(\S+))?')
        if (-not $imageMatch.Success) {
            Write-Bad 'mock 未报告图片计数'
        }
        else {
            $count = [int]$imageMatch.Groups[1].Value
            $types = $imageMatch.Groups[2].Value
            Write-Ok "mock 收到 images=$count types=$types"

            if ($WithImage -and $count -ge 1) { Write-Ok '图片已送达服务端' }
            elseif ($WithImage) { Write-Bad '附加了图片但服务端未收到' }
            elseif ($count -eq 0) { Write-Ok '未附加图片时确实不发送图片块' }

            if ($WithImage -and $types -match 'image/png') { Write-Ok '媒体类型正确传递' }
        }

        # 加载项日志会记录本轮附带的图片数量。
        $addinLog = if (Test-Path -LiteralPath $logFile) {
            Get-Content -LiteralPath $logFile -Raw -Encoding UTF8
        } else { '' }

        if ($WithImage) {
            if ($addinLog -match '本轮附带 (\d+) 张图片') {
                Write-Ok "加载项记录：本轮附带 $($Matches[1]) 张图片"
            }
            else {
                Write-Bad '加载项日志未记录图片附带情况'
            }
        }

        if ($addinLog -match 'can only be accessed from the UI thread') {
            Write-Bad '存在 UI 线程访问错误'
        }
        else {
            Write-Ok '无 UI 线程访问错误'
        }

        return
    }

    Write-Step '撤销与恢复'
    # 撤销必须真正回退单元格内容，而不只是界面变个状态。
    try {
        $sheet = $app.ActiveWorkbook.Worksheets.Item(1)
        $beforeUndo = $sheet.Range('A1').Value2

        $label = $automation.ClickUndoForTest(0)
        Start-Sleep -Seconds 2
        $afterUndo = $sheet.Range('A1').Value2
        Write-Ok "点击「$label」后 A1 = $(if ($null -eq $afterUndo) { '<空>' } else { $afterUndo })"

        if ($label -notmatch '撤销') {
            Write-Bad "首次点击的按钮文字应为「撤销」，实际为「$label」"
        }
        elseif ("$afterUndo" -ne "$beforeUndo") {
            Write-Ok "撤销已回退内容（$beforeUndo → $(if ($null -eq $afterUndo) { '<空>' } else { $afterUndo })）"
        }
        else {
            Write-Bad "撤销后 A1 仍为 $afterUndo，内容未回退"
        }

        $label2 = $automation.ClickUndoForTest(0)
        Start-Sleep -Seconds 2
        $afterRedo = $sheet.Range('A1').Value2
        Write-Ok "点击「$label2」后 A1 = $(if ($null -eq $afterRedo) { '<空>' } else { $afterRedo })"

        if ($label2 -notmatch '恢复') {
            Write-Bad "撤销后按钮文字应变为「恢复」，实际为「$label2」"
        }
        elseif ("$afterRedo" -eq "$beforeUndo") {
            Write-Ok '恢复已还原内容'
        }
        else {
            Write-Bad "恢复后 A1 为 $afterRedo，期望 $beforeUndo"
        }
    }
    catch {
        Write-Bad "撤销验证失败：$($_.Exception.Message)"
    }

    Write-Step '工作簿实际内容'
    # 工具报告成功不等于数据真的写进去了，必须读回单元格核对。
    try {
        $sheet = $app.ActiveWorkbook.Worksheets.Item(1)
        $cells = @{}
        foreach ($addr in 'A1', 'B1', 'A2', 'B2') {
            $cells[$addr] = $sheet.Range($addr).Value2
        }

        foreach ($addr in 'A1', 'B1', 'A2', 'B2') {
            Write-Host "      $addr = $($cells[$addr])"
        }

        $expected = @{ A1 = '名称'; B1 = '数量'; A2 = '铅笔'; B2 = 10 }
        $mismatch = @()
        foreach ($addr in $expected.Keys) {
            if ("$($cells[$addr])" -ne "$($expected[$addr])") {
                $mismatch += "$addr 期望 $($expected[$addr]) 实际 $($cells[$addr])"
            }
        }

        if ($mismatch.Count -eq 0) {
            Write-Ok '单元格内容与预期一致，写入真实生效'
        }
        else {
            Write-Bad ('单元格内容不符：' + ($mismatch -join '；'))
        }
    }
    catch {
        Write-Bad "读回单元格失败：$($_.Exception.Message)"
    }

    Write-Step '判定'
    $log = if (Test-Path -LiteralPath $logFile) { Get-Content -LiteralPath $logFile -Encoding UTF8 -Raw } else { '' }
    $threadError = $log -match 'can only be accessed from the UI thread'
    if ($threadError) { Write-Bad '仍存在 UI 线程访问错误' } else { Write-Ok '无 UI 线程访问错误' }
    if ($log -match '开始对话') { Write-Ok '对话已发起' } else { Write-Bad '未见对话发起记录' }
    if ($log -match '工具 \w+ 执行成功') { Write-Ok '工具已执行' } else { Write-Bad '未见工具执行记录' }
    if ($log -match '对话结束') { Write-Ok '对话已正常收尾' } else { Write-Bad '对话未正常收尾' }

    # 上下文圆环：每步都应推送一次，数值应随对话增长。
    # 日志形如「上下文圆环[路由进入]：444/100000 tokens = 0%」，来源标签可选。
    $ringLines = @([regex]::Matches($log, '上下文圆环(?:\[[^\]]*\])?：(\d+)/(\d+) tokens = (\d+)%'))
    if ($ringLines.Count -eq 0) {
        Write-Bad '上下文圆环未更新'
    }
    else {
        $values = $ringLines | ForEach-Object { [int]$_.Groups[1].Value }
        $grew = ($values | Select-Object -Last 1) -gt ($values | Select-Object -First 1)
        Write-Ok "上下文圆环更新 $($ringLines.Count) 次：$($values -join ' → ') tokens"
        if ($grew) { Write-Ok '圆环数值随对话增长' } else { Write-Note '圆环数值未增长（对话体量过小时属正常）' }
    }

    # 对话结束后的布局：工具卡片应默认折叠，消息应按 4/5 宽度分列。
    $chatLayout = [regex]::Match(
        $log,
        '对话布局：工具卡片 (\d+) 个（展开 (\d+)）\s*助手消息宽 (\S+)\s*用户消息宽 (\S+)\s*欢迎语 (\d+) 个')
    if (-not $chatLayout.Success) {
        Write-Note '未见对话布局上报'
    }
    else {
        $total = [int]$chatLayout.Groups[1].Value
        $opened = [int]$chatLayout.Groups[2].Value
        Write-Ok "工具卡片 $total 个，展开 $opened 个；助手宽 $($chatLayout.Groups[3].Value)，用户宽 $($chatLayout.Groups[4].Value)"

        if ($total -eq 0) { Write-Note '本轮未产生工具卡片' }
        elseif ($opened -eq 0) { Write-Ok '工具卡片默认折叠' }
        else { Write-Bad "有 $opened 个卡片默认就是展开的" }

        # 4/5 即 80%，允许边框与内边距带来的少量偏差。
        foreach ($pair in @(
            @{ Label = '助手消息'; Value = $chatLayout.Groups[3].Value },
            @{ Label = '用户消息'; Value = $chatLayout.Groups[4].Value })) {
            if ($pair.Value -match '^(\d+)%$') {
                $percent = [int]$Matches[1]
                if ($percent -le 82) { Write-Ok "$($pair.Label)宽度 $percent% 未超过 4/5" }
                else { Write-Bad "$($pair.Label)宽度 $percent% 超过了 4/5" }
            }
        }

        if ([int]$chatLayout.Groups[5].Value -ge 1) { Write-Ok '欢迎语已显示' }
        else { Write-Bad '欢迎语未显示' }
    }

    # 达到阈值时应记录压缩，并由界面提示。
    if ($Scenario -eq 'bulk') {
        if ($log -match '上下文压缩：') { Write-Ok '已触发上下文压缩' } else { Write-Bad '预算已调小但未触发压缩' }
        if ($log -match '已达阈值') { Write-Ok '圆环已标记达到阈值' } else { Write-Bad '圆环未标记达到阈值' }
    }
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
