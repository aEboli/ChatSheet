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
# 常用名单也落盘，必须一起备份并在开跑前清空：不清的话上一次留下的星标会让
# 本次的标星动作变成「取消标星」，筛选于是什么都不收起，而断言仍然会通过——
# 一次假绿。
$FavoritesPath = Join-Path $env:LOCALAPPDATA 'ChatSheet\favorite-models.json'
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
    foreach ($p in @($SettingsPath, $SecretPath, $FavoritesPath)) {
        if (Test-Path -LiteralPath $p) {
            Copy-Item -LiteralPath $p -Destination ($p + $BackupSuffix) -Force
        }
    }
    # 名单从零开始，否则上一次跑剩的星标会让本次的标星变成取消标星。
    if (Test-Path -LiteralPath $FavoritesPath) {
        Remove-Item -LiteralPath $FavoritesPath -Force
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

    Write-Step '常用名单：标星、拨开关，筛选真的生效后仍要能切换模型'
    # 筛选最容易出的错是「开了开关就选不动了」：被筛掉的行不在 DOM 里。
    # 要测到这件事，得让筛选真的收起点东西——所以先把当前模型切成会留在名单里的
    # 那个，否则另一个模型会以「当前模型」的身份留在列表里，等于没筛。
    Write-Ok ("标星前：" + $auto.DrivePickerForTest('favorites'))

    $auto.DrivePickerForTest('pick-model:mock-model') | Out-Null
    Start-Sleep -Milliseconds 800

    $starred = $auto.DrivePickerForTest('star:mock-model')
    Start-Sleep -Milliseconds 800
    Assert '能给模型标星' ($starred -match '已标星') $starred

    # 拨开关会清掉「刚标过星，本次先不收起」那个豁免，于是筛选立即生效。
    $toggled = $auto.DrivePickerForTest('toggle-only-favorites')
    Start-Sleep -Milliseconds 800
    Assert '能拨动「只看名单」开关' ($toggled -match 'true') $toggled

    $after = $auto.DrivePickerForTest('favorites')
    Write-Ok ("拨动后：" + $after)
    Assert '筛选真的收起了模型（否则下一条断言等于没测）' ($after -match '收起说明=已按名单收起') $after

    $filtered = $auto.DrivePickerForTest('models')
    Assert '名单里的模型仍在列表里' ($filtered -match 'mock-model') $filtered
    Assert '不在名单里的模型已被收起' ($filtered -notmatch 'mock-model-mini') $filtered

    # 关键一条：筛选生效后仍能切换。被收起的模型要先「显示全部」才拿得到。
    $result = $auto.DrivePickerForTest('pick-model:mock-model')
    Start-Sleep -Milliseconds 800
    $state = $auto.DrivePickerForTest('state')
    Assert '筛选生效后仍能选名单里的模型' ($result -match '已选择' -and $state -match 'mock-model') "$result / $state"

    # 复原：关掉开关，并把模型切回后面断言期望的那个。
    $auto.DrivePickerForTest('toggle-only-favorites') | Out-Null
    Start-Sleep -Milliseconds 600
    $auto.DrivePickerForTest('pick-model:mock-model-mini') | Out-Null
    Start-Sleep -Milliseconds 800
    $restored = $auto.DrivePickerForTest('state')
    Assert '关掉开关后被收起的模型重新可选' ($restored -match 'mock-model-mini') $restored

    Write-Step '按需确认：三种结论在真实宿主里都要能得出'
    # mock 按模型名分流：absent 点名模型（不可用）、ratelimit 限流（未知）、
    # aliasbroken 是 200 但体内含错误（不可用，这条专盯 Chat Completions 新补的
    # error 分支）、silent 是 200 但零事件（未知）。
    #
    # 这四条一起验的是同一件事：判「不可用」必须要求服务端点名模型，
    # 而账号/网络类失败一律判未知——否则一次限流就会给模型判死刑。
    $probeCases = @(
        @{ Model = 'mock-model';       Expect = '可用';   Why = '正常模型' },
        @{ Model = 'mock-absent';      Expect = '不可用'; Why = '404 点名模型' },
        @{ Model = 'mock-aliasbroken'; Expect = '不可用'; Why = '200 但体内含错误' },
        @{ Model = 'mock-ratelimit';   Expect = '未确认'; Why = '429 说的是账号' },
        @{ Model = 'mock-keybad';      Expect = '未确认'; Why = '403 只说密钥' },
        @{ Model = 'mock-silent';      Expect = '未确认'; Why = '200 但零事件' }
    )

    # 状态字段按 `状态=<值>` 全等比对，不用 -match 找子串：
    # 「可用」是「不可用」的子串，找子串时一个其实不可用的模型会通过
    # 「判为可用」这条断言。
    function Get-Field {
        param([string]$Text, [string]$Name)

        foreach ($part in ($Text -split '\|')) {
            $pair = $part.Trim()
            if ($pair.StartsWith("$Name=")) { return $pair.Substring($Name.Length + 1).Trim() }
        }
        return ''
    }

    foreach ($case in $probeCases) {
        $before = $auto.DrivePickerForTest("verdict:$($case.Model)")
        Assert "$($case.Model) 确认前是未确认且带「试一下」" `
            ((Get-Field $before '状态') -eq '未确认' -and $before -match '有试一下=true') $before

        # 未确认的行不该带任何结论标记：颜色是这次改动的核心，
        # 判定还没得出就上色等于告诉用户一个不存在的结论。
        Assert "$($case.Model) 确认前不带结论标记" `
            ((Get-Field $before '标记') -eq '无') $before

        $clicked = $auto.DrivePickerForTest("probe:$($case.Model)")
        Assert "能点 $($case.Model) 的「试一下」" ($clicked -match '已点击') $clicked

        # 探测有 15 秒截止时间，正常情形远快于此。
        $after = ''
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Milliseconds 500
            $after = $auto.DrivePickerForTest("verdict:$($case.Model)")
            if ((Get-Field $after '状态') -ne '正在确认') { break }
        }

        Assert "$($case.Model) 判为 $($case.Expect)（$($case.Why)）" `
            ((Get-Field $after '状态') -eq $case.Expect) $after

        # 结论要在行上看得出来，不是只藏在悬停说明里。
        $expectedMark = switch ($case.Expect) {
            '可用' { '可用标记' }
            '不可用' { '红字' }
            default { '无' }
        }
        Assert "$($case.Model) 的结论在行上有标记（$expectedMark）" `
            ((Get-Field $after '标记') -eq $expectedMark) $after

        # 结论的说明收进悬停：行上不再为每个模型多占一行小字。
        Assert "$($case.Model) 的说明在悬停里" `
            ((Get-Field $after '悬停') -match [regex]::Escape($case.Model)) $after
    }

    # 已有判定的行不再挂「试一下」：那只是噪音，而这一列横向本来就紧。
    $done = $auto.DrivePickerForTest('verdict:mock-absent')
    Assert '已有判定的行不再显示「试一下」' ($done -match '有试一下=false') $done

    Write-Step '「试一下」平时藏着，悬停才浮出来'
    # 断言 class 存在证明不了藏没藏——按钮一直在 DOM 里。只有算出来的 opacity
    # 能说明规则真的生效：选择器写错或变量取不到值时 class 都还在，按钮却常显。
    #
    # 需要一个仍未确认的模型。上面六个都已经有结论，因此手填一个新 ID：
    # 手填的会进名单并成为当前模型，判定仍是未确认。
    $auto.DrivePickerForTest('manual:mock-fresh') | Out-Null
    Start-Sleep -Milliseconds 800

    $vis = $auto.DrivePickerForTest('probe-visible:mock-fresh')
    Write-Ok "「试一下」的可见性：$vis"
    Assert '「试一下」在 DOM 里' ($vis -match '在DOM=true') $vis
    Assert '不悬停时透明度为 0' ((Get-Field $vis '透明度') -eq '0') $vis
    Assert '不悬停时不可点' ((Get-Field $vis '可点') -eq 'False' -or $vis -match '可点=false') $vis

    Write-Step '思考等级：一行一档，说明收进悬停'
    foreach ($level in @('Off', 'Minimal', 'High')) {
        $row = $auto.DrivePickerForTest("thinking-row:$level")
        Write-Ok "$level 行：$row"
        Assert "$level 行上没有说明文字" ((Get-Field $row '行内说明') -eq '无') $row
        Assert "$level 的说明在悬停里" ((Get-Field $row '悬停') -ne '无') $row
        # 一行一档：行高应当只有一行文字的量级。两行就说明又折回去了。
        $height = [int](Get-Field $row '高')
        Assert "$level 只占一行（高 ${height}px）" ($height -le 30) $row
    }

    # 档位段拿到整个浮层的宽度：这是上下分段而不是左右分栏的全部目的。
    $offRow = $auto.DrivePickerForTest('thinking-row:Off')
    $offWidth = [int](Get-Field $offRow '宽')
    Assert "档位行拿到整段宽度（$offWidth px）" ($offWidth -ge 240) $offRow

    Write-Step '切换思考等级'
    # 档位名用英文原名，与协议参数取值逐字一致（见 Providers/Thinking.cs）。
    foreach ($level in @('Low', 'Max', 'Off')) {
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
    foreach ($p in @($SettingsPath, $SecretPath, $FavoritesPath)) {
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
