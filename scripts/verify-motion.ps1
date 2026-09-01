<#
.SYNOPSIS
验证对话界面的动效：对话流进场动画与顶栏图标的点击回弹。

.DESCRIPTION
两层一起跑：

  一、Node 侧的静态核对（tests/web/chat-motion.test.mjs）。锁住关键帧名与
      animation 引用逐字一致、两个动画类只带 animation 不带静态样式、
      重放与首挂/重挂的判据顺序、方向角、后写规则的覆盖。

  二、真实 WebView2 里的实测（PaneHarness --motion）。这一层不可省：
      「动画此刻在跑没跑、播到第几毫秒」只有真实渲染器算得出来。它当场抓到过
      一个前一层全绿的缺陷——append 一个已在场的节点会把运行中的动画取消并
      从头重播（进度 170ms 退回 0ms），表现是那个气泡可见地闪两下。

不需要 Excel，也不动用户的配置：PaneHarness 自带宿主，动效检查用注入的推送
驱动面板自己的代码路径，不连真实网关。
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Static', 'Live')]
    [string]$Scope = 'All'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step { param([string]$T) Write-Host "==> $T" -ForegroundColor Cyan }
function Write-Ok { param([string]$T) Write-Host "    $T" -ForegroundColor Green }
function Write-Bad { param([string]$T) Write-Host "    $T" -ForegroundColor Red }

$failed = 0

if ($Scope -in @('All', 'Static')) {
    Write-Step '静态核对（Node）'
    & node (Join-Path $RepoRoot 'tests\web\chat-motion.test.mjs')
    if ($LASTEXITCODE -eq 0) { Write-Ok '静态核对通过' }
    else { $failed++; Write-Bad "静态核对失败（退出码 $LASTEXITCODE）" }
}

if ($Scope -in @('All', 'Live')) {
    Write-Step '真实 WebView2 实测'

    $harness = Join-Path $RepoRoot 'tests\ChatSheet.PaneHarness\bin\Debug\ChatSheet.PaneHarness.exe'
    if (-not (Test-Path -LiteralPath $harness)) {
        $failed++
        Write-Bad "找不到 $harness，先构建解决方案（Debug）"
    }
    else {
        & $harness --motion
        if ($LASTEXITCODE -eq 0) { Write-Ok '实测通过' }
        else { $failed++; Write-Bad "实测失败（退出码 $LASTEXITCODE）" }
    }
}

Write-Host ''
if ($failed -eq 0) {
    Write-Host '=== 动效验证：全部通过 ===' -ForegroundColor Green
    exit 0
}

Write-Host "=== 动效验证：$failed 层失败 ===" -ForegroundColor Red
exit 1
