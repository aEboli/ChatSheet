<#
.SYNOPSIS
生成可上传到 GitHub Release 的 ChatSheet Windows ZIP。

.DESCRIPTION
从当前源码构建 Release，然后将完整加载项输出、安装脚本和发布文档暂存为
ChatSheet-v<版本>-win\，生成 ZIP、包内 SHA256SUMS.txt 与 ZIP 的 SHA-256 sidecar。
产物始终写入仓库已忽略的 artifacts\release\，不会提交构建二进制。

.PARAMETER Version
可选。必须与 src\ChatSheet.AddIn\ChatSheet.AddIn.csproj 中的 Version 完全一致；
省略时使用项目版本。
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src\ChatSheet.AddIn\ChatSheet.AddIn.csproj'
$BuildOutput = Join-Path $RepoRoot 'src\ChatSheet.AddIn\bin\Release'
$ReleaseRoot = Join-Path $RepoRoot 'artifacts\release'

function Write-Step { param([string]$Text) Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok { param([string]$Text) Write-Host "    $Text" -ForegroundColor Green }

function Get-AddInVersion {
    if (-not (Test-Path -LiteralPath $ProjectPath)) {
        throw "未找到加载项项目：$ProjectPath"
    }

    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
    $versions = @(
        $project.Project.PropertyGroup |
            ForEach-Object { ([string]$_.Version).Trim() } |
            Where-Object { $_ }
    )

    if ($versions.Count -ne 1) {
        throw "无法从 $ProjectPath 唯一确定 Version。"
    }

    return $versions[0]
}

function Assert-ReleasePath {
    param([Parameter(Mandatory)][string]$Path)

    $root = [System.IO.Path]::GetFullPath($ReleaseRoot).TrimEnd('\', '/')
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = "$root\"

    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝操作 artifacts\\release 之外的路径：$fullPath"
    }

    return $fullPath
}

function Remove-ReleasePath {
    param([Parameter(Mandatory)][string]$Path)

    $safePath = Assert-ReleasePath -Path $Path
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }
}

function Assert-RequiredFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label 缺失：$Path"
    }
}

$projectVersion = Get-AddInVersion
if ($Version) {
    if ($Version -ne $projectVersion) {
        throw "指定版本 $Version 与项目版本 $projectVersion 不一致。"
    }
} else {
    $Version = $projectVersion
}

$packageName = "ChatSheet-v$Version-win"
$stageDirectory = Join-Path $ReleaseRoot $packageName
$zipPath = Join-Path $ReleaseRoot "$packageName.zip"
$zipHashPath = "$zipPath.sha256"
$payloadDirectory = Join-Path $stageDirectory 'app'
$scriptsDirectory = Join-Path $stageDirectory 'scripts'
$releaseNotesSource = Join-Path $RepoRoot "docs\releases\v$Version.md"

Write-Step "构建 ChatSheet v$Version"
Push-Location $RepoRoot
try {
    & dotnet build $ProjectPath --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "构建失败，退出码 $LASTEXITCODE。"
    }
} finally {
    Pop-Location
}

Assert-RequiredFile -Path (Join-Path $BuildOutput 'ChatSheet.AddIn.dll') -Label '加载项程序集'
Assert-RequiredFile -Path (Join-Path $BuildOutput 'web\index.html') -Label 'WebView2 面板入口'

Write-Step '暂存发布包'
New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
Remove-ReleasePath -Path $stageDirectory
Remove-ReleasePath -Path $zipPath
Remove-ReleasePath -Path $zipHashPath

New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $scriptsDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $BuildOutput '*') -Destination $payloadDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination (Join-Path $scriptsDirectory 'install.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ChatSheet.Registration.psm1') -Destination (Join-Path $scriptsDirectory 'ChatSheet.Registration.psm1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'docs\windows-release-install.md') -Destination (Join-Path $stageDirectory 'INSTALL.md') -Force
Copy-Item -LiteralPath $releaseNotesSource -Destination (Join-Path $stageDirectory 'RELEASE-NOTES.md') -Force

$requiredPackageFiles = @(
    @{ Path = (Join-Path $payloadDirectory 'ChatSheet.AddIn.dll'); Label = '加载项程序集' },
    @{ Path = (Join-Path $payloadDirectory 'web\index.html'); Label = 'WebView2 面板入口' },
    @{ Path = (Join-Path $scriptsDirectory 'install.ps1'); Label = '安装脚本' },
    @{ Path = (Join-Path $scriptsDirectory 'ChatSheet.Registration.psm1'); Label = '注册模块' },
    @{ Path = (Join-Path $stageDirectory 'INSTALL.md'); Label = '安装说明' },
    @{ Path = (Join-Path $stageDirectory 'RELEASE-NOTES.md'); Label = '发行说明' }
)
foreach ($required in $requiredPackageFiles) {
    Assert-RequiredFile -Path $required.Path -Label $required.Label
}

Write-Step '生成包内 SHA-256 清单'
$stagePrefix = ([System.IO.Path]::GetFullPath($stageDirectory)).TrimEnd('\', '/') + '\'
$manifestLines = @(
    Get-ChildItem -LiteralPath $stageDirectory -Recurse -File |
        Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        Sort-Object FullName |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $relativePath = $_.FullName.Substring($stagePrefix.Length).Replace('\', '/')
            "$hash  $relativePath"
        }
)
[System.IO.File]::WriteAllLines(
    (Join-Path $stageDirectory 'SHA256SUMS.txt'),
    $manifestLines,
    [System.Text.UTF8Encoding]::new($false))

Write-Step '压缩 Windows 发布包'
Compress-Archive -Path $stageDirectory -DestinationPath $zipPath -CompressionLevel Optimal -Force
Assert-RequiredFile -Path $zipPath -Label '发布 ZIP'

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(
    $zipHashPath,
    "$zipHash  $([System.IO.Path]::GetFileName($zipPath))$([Environment]::NewLine)",
    [System.Text.UTF8Encoding]::new($false))

Write-Ok "发布 ZIP：$zipPath"
Write-Ok "SHA-256：$zipHash"
Write-Ok "校验文件：$zipHashPath"

[pscustomobject]@{
    Version = $Version
    Archive = $zipPath
    ArchiveSHA256 = $zipHash
    ChecksumFile = $zipHashPath
    PackageDirectory = $stageDirectory
}
