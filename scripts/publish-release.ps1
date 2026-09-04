<#
.SYNOPSIS
建 GitHub Release 并上传发行资产。

.DESCRIPTION
凭据从 git 的凭据系统取（Windows 凭据管理器），不落盘、不打印。
这台机器没有 gh CLI，因此直接调 REST API。

幂等：Release 已存在时更新它而不是报错；资产同名时先删旧的再传，
免得 GitHub 追加成 "xxx-1.zip" 那种名字——README 里写死了文件名。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$NotesPath,
    [string[]]$Assets = @(),
    [string]$Repo = 'aEboli/ChatSheet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Write-Step { param([string]$T) Write-Host "==> $T" -ForegroundColor Cyan }
function Write-Ok   { param([string]$T) Write-Host "    $T" -ForegroundColor Green }
function Write-Bad  { param([string]$T) Write-Host "    $T" -ForegroundColor Red }

# ---- 取凭据。只在内存里传递，不写文件、不进日志 ----
function Get-GitHubToken {
    # 两个坑叠在一起，都会得到「missing protocol field」：
    #
    # 一、不能叫 $input——那是 PowerShell 的自动变量（管道输入枚举器），
    #     赋值后再管道传出去，git 收到的是空输入。
    # 二、不能用管道喂 git。PowerShell 把字符串交给原生命令时按控制台编码
    #     重写行尾，而 git 的凭据解析器按换行切字段，收到回车就认不出来。
    #
    # 因此写一个只含换行符的临时文件，用 cmd 的重定向喂进去。
    $tmp = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($tmp, "protocol=https`nhost=github.com`n`n", (New-Object Text.UTF8Encoding($false)))
        $out = & cmd /c "git credential fill < `"$tmp`"" 2>$null
    }
    finally {
        Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
    }
    foreach ($line in $out) {
        if ($line -like 'password=*') { return $line.Substring('password='.Length) }
    }
    throw '取不到 github.com 的凭据。git push 能成的话它应该在 Windows 凭据管理器里。'
}

$token = Get-GitHubToken
if (-not $token) { throw '凭据为空' }

$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'ChatSheet-release-script'
}

# ---- 先确认凭据可用且有写权限 ----
Write-Step '核对凭据'
try {
    $me = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $headers -Method Get
    Write-Ok "身份：$($me.login)"
}
catch {
    Write-Bad "凭据不可用：$($_.Exception.Message)"
    throw
}

$repoInfo = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo" -Headers $headers -Method Get
if (-not $repoInfo.permissions.push) {
    throw "凭据对 $Repo 没有写权限，建不了 Release。"
}
Write-Ok "对 $Repo 有写权限"

# ---- 正文 ----
if (-not (Test-Path -LiteralPath $NotesPath)) { throw "找不到发行说明：$NotesPath" }
$notes = [System.IO.File]::ReadAllText((Resolve-Path $NotesPath), [System.Text.UTF8Encoding]::new($false))
Write-Ok "发行说明 $([math]::Round($notes.Length / 1024, 1)) KB"

# ---- 已存在就更新，否则新建 ----
Write-Step "处理 Release $Tag"
$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" `
        -Headers $headers -Method Get
    Write-Ok "已存在（id=$($release.id)），改为更新"
}
catch {
    Write-Ok '不存在，新建'
}

$body = @{
    tag_name = $Tag
    name     = $Title
    body     = $notes
    draft    = $false
    # 预发布判定交给版本号本身：0.x 仍在快速变动，但这里按显式约定发正式版。
    prerelease = $false
} | ConvertTo-Json -Depth 4

$bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($body)

if ($release) {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/$($release.id)" `
        -Headers $headers -Method Patch -Body $bodyBytes -ContentType 'application/json; charset=utf-8'
    Write-Ok "已更新：$($release.html_url)"
}
else {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases" `
        -Headers $headers -Method Post -Body $bodyBytes -ContentType 'application/json; charset=utf-8'
    Write-Ok "已创建：$($release.html_url)"
}

# ---- 资产 ----
foreach ($asset in $Assets) {
    if (-not (Test-Path -LiteralPath $asset)) { throw "找不到资产：$asset" }
    $file = Get-Item -LiteralPath $asset
    Write-Step "上传 $($file.Name)（$([math]::Round($file.Length / 1KB, 0)) KB）"

    # 同名旧资产先删。GitHub 不覆盖，会追加成 xxx-1.zip，而 README 写死了文件名。
    $existing = $release.assets | Where-Object { $_.name -eq $file.Name }
    foreach ($old in $existing) {
        Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/assets/$($old.id)" `
            -Headers $headers -Method Delete | Out-Null
        Write-Ok '已删除同名旧资产'
    }

    $type = if ($file.Extension -eq '.zip') { 'application/zip' } else { 'text/plain' }
    $uploadUrl = ($release.upload_url -replace '\{\?name,label\}', '') + "?name=$($file.Name)"

    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $result = Invoke-RestMethod -Uri $uploadUrl -Headers $headers -Method Post `
        -Body $bytes -ContentType $type
    Write-Ok "已上传：$($result.browser_download_url)"
}

Write-Host ''
Write-Host "Release 地址：$($release.html_url)" -ForegroundColor Green
