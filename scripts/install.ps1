<#
.SYNOPSIS
ChatSheet 安装 / 修复 / 卸载 / 诊断。

.DESCRIPTION
支持两种布局：源码检出会先构建再安装；已解压的官方发布 ZIP 会直接使用 app\ 中的预构建产物。
两种布局均把加载项文件复制到当前用户的 LocalAppData，向 HKLM 注册托管 COM 类，
并仅为当前用户登记 Microsoft Excel 加载项。因此安装和卸载会请求管理员 UAC 授权。
运行时需要 .NET Framework 4.8 与 WebView2 Runtime。源码安装需要 .NET SDK；预构建 ZIP 不需要。
日常安装和运行不需要 Node.js、开发证书或环境变量。

.PARAMETER Action
install   构建（如有 SDK）并安装、注册
uninstall 反注册并删除安装目录
diagnose  输出环境与注册状态
#>
[CmdletBinding()]
param(
    [ValidateSet('install', 'uninstall', 'diagnose')]
    [string]$Action = 'install',

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AssemblyName = 'ChatSheet.AddIn'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src\ChatSheet.AddIn\ChatSheet.AddIn.csproj'
$PrebuiltPayload = Join-Path $RepoRoot 'app'
$IsPrebuiltPackage = (Test-Path -LiteralPath (Join-Path $PrebuiltPayload "$AssemblyName.dll")) -and
    -not (Test-Path -LiteralPath $ProjectPath)
$BuildOutput = if ($IsPrebuiltPackage) {
    $PrebuiltPayload
} else {
    Join-Path $RepoRoot 'src\ChatSheet.AddIn\bin\Release'
}
$InstallDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\app'

Import-Module (Join-Path $PSScriptRoot 'ChatSheet.Registration.psm1') -Force

function Write-Step { param([string]$Text) Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host "    $Text" -ForegroundColor Green }
function Write-Warn2{ param([string]$Text) Write-Host "    $Text" -ForegroundColor Yellow }
function Write-Bad  { param([string]$Text) Write-Host "    $Text" -ForegroundColor Red }

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
.SYNOPSIS
安装与卸载需要管理员权限，未提权时经 UAC 重新启动自身。

.DESCRIPTION
托管 COM 类只能注册到 HKLM（mscoree 不读 HKCU 下的类注册），
因此安装和卸载各需要一次管理员授权。诊断只读，不需要提权。
#>
function Assert-Elevated {
    param([Parameter(Mandatory)][string]$ForAction)

    if (Test-Elevated) {
        return
    }

    Write-Warn2 "注册托管 COM 类需要写入 HKLM，正在请求管理员权限…"

    $arguments = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', "`"$PSCommandPath`""
        '-Action', $ForAction
    )
    if ($SkipBuild) { $arguments += '-SkipBuild' }

    try {
        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -PassThru -Wait
        exit $process.ExitCode
    }
    catch {
        throw "提权被取消或失败，无法继续。原因：$($_.Exception.Message)"
    }
}

function Get-RunningHosts {
    Get-Process -Name 'EXCEL', 'et', 'wps' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty ProcessName -Unique
}

<#
.SYNOPSIS
仅在会真正发生文件占用时拦截。

.DESCRIPTION
首次安装时安装目录不存在，没有文件被占用，此时无需退出宿主，
只需在安装后重启宿主即可生效。
覆盖安装会替换正在被宿主加载的 DLL，那时必须先退出宿主，
否则复制会失败或留下半新半旧的产物。
#>
function Assert-CanWritePayload {
    $running = Get-RunningHosts
    if (-not $running) {
        return
    }

    # 直接实测文件是否被占用，而不是靠“进程在运行”推断：
    # 宿主进程在运行但尚未加载过本加载项时，文件并未被锁定，此时覆盖安装是安全的。
    $dll = Join-Path $InstallDir "$AssemblyName.dll"
    if (Test-Path -LiteralPath $dll) {
        try {
            $stream = [System.IO.File]::Open($dll, 'Open', 'ReadWrite', 'None')
            $stream.Dispose()
        }
        catch {
            $names = $running -join '、'
            throw "检测到 $names 已加载旧版本加载项，文件正被占用。请先保存并完全退出这些程序，然后重新执行。"
        }
    }

    Write-Warn2 ("检测到 " + ($running -join '、') + " 正在运行；文件未被占用，可继续安装，但需要重启它们才能生效。")
}

function Invoke-Build {
    if ($IsPrebuiltPackage) {
        Write-Step '使用预构建发布包'
        Write-Ok "已检测到发布包载荷：$BuildOutput"
        return
    }

    Write-Step '构建加载项'

    if (-not (Test-Path -LiteralPath $ProjectPath)) {
        throw "未找到源码项目：$ProjectPath。请从完整源码仓库安装，或解压完整的 ChatSheet 发布 ZIP 后再执行。"
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw '未找到 dotnet SDK，无法从源码构建。若已有预编译产物，请改用 -SkipBuild。'
    }

    Push-Location $RepoRoot
    try {
        & dotnet build $ProjectPath --configuration Release --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "构建失败，退出码 $LASTEXITCODE。"
        }
    }
    finally {
        Pop-Location
    }

    Write-Ok '构建完成'
}

<#
.SYNOPSIS
检查生成产物是否落后于源文件。

.DESCRIPTION
侧边栏的 HTML/JS/CSS 是靠构建复制到 bin 目录的。
若改过这些文件却用 -SkipBuild 安装，会静默部署旧版本，
表现为「改了界面却没变化」，极难察觉。这里显式拦住。
#>
function Assert-BuildUpToDate {
    if ($IsPrebuiltPackage) {
        return
    }

    $webSource = Join-Path $RepoRoot 'src\web'
    if (-not (Test-Path -LiteralPath $webSource)) { return }

    $newestSource = Get-ChildItem -LiteralPath $webSource -Recurse -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if (-not $newestSource) { return }

    $builtCounterpart = Join-Path $BuildOutput 'web'
    if (-not (Test-Path -LiteralPath $builtCounterpart)) {
        throw '生成目录中没有 web 文件夹，请先构建（不要带 -SkipBuild）。'
    }

    $newestBuilt = Get-ChildItem -LiteralPath $builtCounterpart -Recurse -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if (-not $newestBuilt -or $newestSource.LastWriteTimeUtc -gt $newestBuilt.LastWriteTimeUtc) {
        throw "侧边栏源文件（$($newestSource.Name)）比生成产物新。请去掉 -SkipBuild 重新构建，否则会部署旧界面。"
    }
}

function Copy-Payload {
    Write-Step '复制文件到安装目录'

    $dll = Join-Path $BuildOutput "$AssemblyName.dll"
    if (-not (Test-Path -LiteralPath $dll)) {
        if ($IsPrebuiltPackage) {
            throw "发布包不完整，缺少预构建载荷：$dll。请重新下载并完整解压 ZIP。"
        }
        throw "未找到生成产物：$dll。请先构建（不要带 -SkipBuild）。"
    }

    Assert-BuildUpToDate

    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    Copy-Item -Path (Join-Path $BuildOutput '*') -Destination $InstallDir -Recurse -Force

    # WebView2 的原生 loader 按位数分目录。AnyCPU 构建需同时保留 x86 与 x64 载荷，
    # 以支持 32 位或 64 位的 Microsoft Excel。
    foreach ($arch in @('x86', 'x64')) {
        $loader = Join-Path $InstallDir "runtimes\win-$arch\native\WebView2Loader.dll"
        if (Test-Path -LiteralPath $loader) {
            Write-Ok "WebView2 loader ($arch) 就位"
        } else {
            Write-Warn2 "缺少 WebView2 loader ($arch)：$loader"
        }
    }

    $webRoot = Join-Path $InstallDir 'web\index.html'
    if (-not (Test-Path -LiteralPath $webRoot)) {
        throw "侧边栏页面缺失：$webRoot"
    }

    Write-Ok "安装目录：$InstallDir"
    return $InstallDir
}

function Get-AssemblyIdentity {
    param([Parameter(Mandatory)][string]$Path)

    # 只读元数据：不加载程序集、不解析依赖，也不会锁定文件。
    $name = [System.Reflection.AssemblyName]::GetAssemblyName($Path)
    [pscustomobject]@{
        FullName = $name.FullName
        # 注册用的版本子键名取程序集版本，务必带四段（如 0.1.0.0）。
        Version  = $name.Version.ToString()
    }
}

function Invoke-Install {
    Assert-Elevated -ForAction 'install'
    Assert-CanWritePayload

    if (-not $SkipBuild) {
        Invoke-Build
    }

    $target = Copy-Payload
    $dll = Join-Path $target "$AssemblyName.dll"

    Write-Step '注册 COM 加载项'
    $identity = Get-AssemblyIdentity -Path $dll
    Register-ChatSheetAddIn -AssemblyFullName $identity.FullName -AssemblyVersion $identity.Version `
        -CodeBase ('file:///' + ($dll -replace '\\', '/'))
    Write-Ok "已注册：$($identity.FullName)"
    Write-Ok '已写入 32 位与 64 位两个注册表视图'

    Write-Step '安装后自检'
    Show-Diagnostics

    Write-Host ''
    Write-Host '安装完成。请重启 Microsoft Excel，在功能区 “ChatSheet” 选项卡点击“ChatSheet 面板”。' -ForegroundColor Green
    Write-Host '首次使用请在面板的“设置”页选择接入模式与模型。' -ForegroundColor Green
}

function Invoke-Uninstall {
    Assert-Elevated -ForAction 'uninstall'

    $running = Get-RunningHosts
    if ($running) {
        throw ("检测到 " + ($running -join '、') + " 正在运行。卸载需要删除正在被加载的文件，请先保存并完全退出这些程序，然后重新执行。")
    }

    Write-Step '反注册'
    Unregister-ChatSheetAddIn
    Write-Ok '注册表项已移除'

    Write-Step '删除安装目录'
    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
        Write-Ok $InstallDir
    } else {
        Write-Warn2 '安装目录不存在，跳过'
    }

    Write-Host ''
    Write-Host '卸载完成。日志与设置仍保留在 %LOCALAPPDATA%\ChatSheet，可手动删除。' -ForegroundColor Green
}

function Show-Diagnostics {
    $wv = $null
    foreach ($p in @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )) {
        if (Test-Path -LiteralPath $p) {
            $wv = (Get-ItemProperty -LiteralPath $p).pv
            break
        }
    }

    $ndp = 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
    $netfx = if (Test-Path -LiteralPath $ndp) { (Get-ItemProperty -LiteralPath $ndp).Version } else { $null }

    Write-Host '  运行时依赖' -ForegroundColor White
    if ($wv)    { Write-Ok "WebView2 运行时 $wv" } else { Write-Bad 'WebView2 运行时缺失，侧边栏无法显示' }
    if ($netfx) { Write-Ok ".NET Framework $netfx" } else { Write-Bad '.NET Framework 4.8 缺失' }

    $state = Get-ChatSheetRegistrationState

    Write-Host '  COM 注册' -ForegroundColor White
    foreach ($row in $state.Classes) {
        $text = "$($row.View) / $($row.Item)"
        if ($row.Registered -and $row.FileExists) {
            Write-Ok "$text 正常"
        } elseif ($row.Registered) {
            Write-Bad "$text 已注册但文件缺失：$($row.CodeBase)"
        } else {
            Write-Bad "$text 未注册"
        }
    }

    Write-Host '  宿主登记' -ForegroundColor White
    foreach ($row in $state.Hosts) {
        if (-not $row.Registered) {
            Write-Bad "$($row.Host) 未登记"
        } elseif ($row.Disabled) {
            Write-Bad "$($row.Host) 已被宿主禁用（LoadBehavior=2），说明加载时抛了异常，请查看日志"
        } else {
            Write-Ok "$($row.Host) LoadBehavior=$($row.LoadBehavior)"
        }
    }

    $logDir = Join-Path $env:LOCALAPPDATA 'ChatSheet\logs'
    Write-Host '  日志' -ForegroundColor White
    if (Test-Path -LiteralPath $logDir) {
        $logs = Get-ChildItem -LiteralPath $logDir -Filter '*.log' -ErrorAction SilentlyContinue
        if ($logs) {
            foreach ($log in $logs) { Write-Ok "$($log.Name)（$([math]::Round($log.Length / 1KB, 1)) KB）" }
        } else {
            Write-Warn2 '尚无日志，说明加载项还没被宿主加载过'
        }
    } else {
        Write-Warn2 "日志目录尚未创建：$logDir"
    }
}

switch ($Action) {
    'install'   { Invoke-Install }
    'uninstall' { Invoke-Uninstall }
    'diagnose'  { Write-Step '环境诊断'; Show-Diagnostics }
}
