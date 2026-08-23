<#
.SYNOPSIS
P0 验证：确认宿主是否真正加载加载项并能创建侧边栏。

.DESCRIPTION
用真实工作簿正常启动宿主（不能用 COM 自动化，自动化启动会跳过 COM 加载项），
等待加载后读取日志与注册表状态判定结果。

会自动清理 Office 的 Resiliency 禁用黑名单：
加载项一旦在早期失败过，宿主会把它永久拉黑，之后即使问题已修复也不再尝试加载。

.PARAMETER Host
excel 或 wps
#>
[CmdletBinding()]
param(
    [ValidateSet('excel', 'wps')]
    [string]$TargetHost = 'excel',

    [int]$WaitSeconds = 15,

    [switch]$KeepOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$LogDir   = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'
$Workbook = Join-Path $RepoRoot 'work\p0-test.xlsx'
$ProgId   = 'ChatSheet.AddIn'

function Write-Step { param([string]$T) Write-Host "==> $T" -ForegroundColor Cyan }
function Write-Ok   { param([string]$T) Write-Host "    $T" -ForegroundColor Green }
function Write-Bad  { param([string]$T) Write-Host "    $T" -ForegroundColor Red }
function Write-Note { param([string]$T) Write-Host "    $T" -ForegroundColor Yellow }

function Clear-DisabledItems {
    # Office 把加载失败过的加载项写进 Resiliency\DisabledItems 并永久跳过，
    # 必须清掉，否则修复了根因也看不到任何变化。
    $cleared = 0
    foreach ($root in @(
        'HKCU:\Software\Microsoft\Office\16.0\Excel\Resiliency\DisabledItems',
        'HKCU:\Software\Kingsoft\Office\6.0\et\Resiliency\DisabledItems',
        'HKCU:\Software\Kingsoft\Office\ET\Resiliency\DisabledItems'
    )) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $key = Get-Item -LiteralPath $root
        foreach ($name in $key.GetValueNames()) {
            $raw = $key.GetValue($name)
            if ($raw -isnot [byte[]]) { continue }
            $text = [System.Text.Encoding]::Unicode.GetString($raw)
            if ($text -match 'chatsheet') {
                Remove-ItemProperty -LiteralPath $root -Name $name -Force
                Write-Note "已清除禁用黑名单项：$root\$name"
                $cleared++
            }
        }
    }

    if ($cleared -eq 0) { Write-Ok '禁用黑名单中无本加载项' }
}

function Stop-Hosts {
    $procs = Get-Process -Name 'EXCEL', 'et', 'wps' -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Note "已结束遗留宿主进程：$(($procs | Select-Object -ExpandProperty ProcessName -Unique) -join '、')"
    }
}

function Get-HostExecutable {
    if ($TargetHost -eq 'excel') {
        $candidates = @(
            'C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE',
            'C:\Program Files (x86)\Microsoft Office\root\Office16\EXCEL.EXE'
        )
    }
    else {
        $base = Join-Path $env:LOCALAPPDATA 'Kingsoft\WPS Office'
        $candidates = @()
        if (Test-Path -LiteralPath $base) {
            # 取版本号最大的安装目录，避免命中旧版本残留。
            $versions = Get-ChildItem -LiteralPath $base -Directory |
                Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
                Sort-Object { [version]$_.Name } -Descending
            foreach ($v in $versions) {
                $candidates += (Join-Path $v.FullName 'office6\et.exe')
            }
        }
    }

    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }

    throw "未找到宿主可执行文件（$TargetHost）。候选：$($candidates -join ' ; ')"
}

Write-Step "P0 宿主加载验证：$TargetHost"

if (-not (Test-Path -LiteralPath $Workbook)) {
    throw "缺少测试工作簿：$Workbook。请先运行 scripts\new-test-workbook.ps1。"
}

Stop-Hosts
Clear-DisabledItems

if (Test-Path -LiteralPath $LogDir) {
    Remove-Item -LiteralPath $LogDir -Recurse -Force
}

$exe = Get-HostExecutable
Write-Ok "宿主：$exe"

Write-Step "启动宿主并打开测试工作簿，等待 $WaitSeconds 秒"
# 必须正常启动并带文档：COM 自动化启动的实例会跳过 COM 加载项。
# 路径必须显式加引号：仓库路径含空格，未加引号会被宿主按空格截断，
# 结果是宿主报「找不到文件」并停在对话框，始终无法完成初始化。
Start-Process -FilePath $exe -ArgumentList "`"$Workbook`"" | Out-Null
Start-Sleep -Seconds $WaitSeconds

$running = Get-Process -Name 'EXCEL', 'et', 'wps' -ErrorAction SilentlyContinue
if ($running) {
    Write-Ok "宿主进程运行中：$(($running | ForEach-Object { "$($_.ProcessName)#$($_.Id)" }) -join '、')"
}
else {
    Write-Bad '宿主进程已退出，无法判定加载结果'
}

Write-Step '加载项日志'
if (Test-Path -LiteralPath $LogDir) {
    # 必须用 @() 强制成数组：StrictMode 下单个结果没有 Count 属性。
    $logs = @(Get-ChildItem -LiteralPath $LogDir -Filter '*.log')
    foreach ($log in $logs) {
        Write-Ok "文件：$($log.Name)"
        Get-Content -LiteralPath $log.FullName -Encoding UTF8 | ForEach-Object { Write-Host "      $_" }
    }
    if ($logs.Count -gt 0) {
        Write-Host ''
        Write-Host '判定：加载项已被宿主加载。' -ForegroundColor Green
    }
}
else {
    Write-Bad '未产生日志，加载项未被加载'
}

Write-Step '加载后状态'
foreach ($hive in @(
    @{ Label = 'Microsoft Excel'; Path = "HKCU:\Software\Microsoft\Office\Excel\Addins\$ProgId" },
    @{ Label = 'WPS 表格 (ET)';   Path = "HKCU:\Software\Kingsoft\Office\ET\Addins\$ProgId" }
)) {
    if (Test-Path -LiteralPath $hive.Path) {
        $lb = (Get-ItemProperty -LiteralPath $hive.Path).LoadBehavior
        # 宿主在加载抛异常后会把 LoadBehavior 改成 2，这是最直接的失败信号。
        if ($lb -eq 2) { Write-Bad "$($hive.Label) LoadBehavior=2（宿主因加载失败已禁用）" }
        else { Write-Ok "$($hive.Label) LoadBehavior=$lb" }
    }
}

Clear-DisabledItems

if (-not $KeepOpen) {
    Stop-Hosts
    Write-Note '已关闭宿主。加 -KeepOpen 可保留窗口手动查看面板。'
}
