# ChatSheet 注册表操作模块。
#
# 关键约束：加载项程序集是 AnyCPU，而两个宿主位数不同
# （Microsoft Excel 为 x64，WPS 表格 et.exe 为 x86），
# 两种位数的 CLR 读取不同的注册表视图，因此 COM 注册必须同时写入
# HKCU\Software\Classes 与 HKCU\Software\Classes\Wow6432Node。
# 全部操作限定在 HKCU，安装和卸载都不需要管理员权限。

Set-StrictMode -Version Latest

# 这些值必须与 src\ChatSheet.AddIn\ComIds.cs 保持一致。
$script:AddInClsid      = '{DC0DBDFD-88B8-4071-9174-39C2627813C8}'
$script:AddInProgId     = 'ChatSheet.AddIn'
$script:AddInClass      = 'ChatSheet.AddIn.ComAddIn'
$script:TaskPaneClsid   = '{0417A068-632B-4CAD-9390-3479277B03CB}'
$script:TaskPaneProgId  = 'ChatSheet.TaskPane'
$script:TaskPaneClass   = 'ChatSheet.AddIn.TaskPaneControl'
$script:RuntimeVersion  = 'v4.0.30319'
$script:FriendlyName    = 'ChatSheet 表格 AI 助手'
$script:Description     = '在表格右侧提供对话式 AI 助手，可读取工作簿并在审批后修改单元格。'

# 托管 COM 类必须注册到 HKLM，不能用 HKCU。
#
# 实测依据：同一个零依赖程序集，按完全相同的键值结构注册到
# HKCU\Software\Classes 时激活报 0x80070002（系统找不到指定的文件），
# 改注册到 HKLM\SOFTWARE\Classes 后 x64 与 x86 均激活成功。
# 原因是承载托管类的 mscoree shim 不读取 HKCU 下的类注册信息。
# VSTO 能做到免提权，是因为它的原生加载器本身注册在 HKLM，
# HKCU 项只指向该加载器，并非直接指向 mscoree。
#
# 两个视图都要写：x64 宿主（Microsoft Excel）读前者，
# x86 宿主（WPS 表格 et.exe）读后者，已实测互不可见。
$script:ClassRoots = @(
    'HKLM:\SOFTWARE\Classes',
    'HKLM:\SOFTWARE\Classes\Wow6432Node'
)

# 加载项登记保留在 HKCU：宿主直接读取这些键、不经 mscoree，因此不受上述限制。
# 放在 HKCU 而非 HKLM 是有意的取舍——只影响当前用户，不改动机器上其他账户的宿主行为。
#
# 只登记 Microsoft Excel。WPS 表格个人版不加载第三方加载项，实测 COM 与 JSAPI
# 两条路都不通（详见 docs\architecture.md），登记它只会产生无用的注册表项。
$script:AddInHives = @(
    @{ Label = 'Microsoft Excel'; Path = 'HKCU:\Software\Microsoft\Office\Excel\Addins' }
)

function Get-ChatSheetIds {
    [CmdletBinding()]
    param()

    [pscustomobject]@{
        AddInClsid     = $script:AddInClsid
        AddInProgId    = $script:AddInProgId
        AddInClass     = $script:AddInClass
        TaskPaneClsid  = $script:TaskPaneClsid
        TaskPaneProgId = $script:TaskPaneProgId
        TaskPaneClass  = $script:TaskPaneClass
        ClassRoots     = $script:ClassRoots
        AddInHives     = $script:AddInHives
    }
}

function New-RegKey {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -Path $Path -Force | Out-Null
    }
}

function Set-RegValue {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()]$Value,
        [string]$Type = 'String'
    )

    New-RegKey -Path $Path
    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

<#
.SYNOPSIS
注册一个 .NET 类到 COM，覆盖 32 位与 64 位两个注册表视图。

.PARAMETER AsActiveXControl
侧边栏控件需要此开关：ICTPFactory.CreateCTP 按 ProgID 实例化控件，
宿主要求该 CLSID 下存在 Control 子键，否则会拒绝创建窗格。
#>
function Register-ComClass {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Clsid,
        [Parameter(Mandatory)][string]$ProgId,
        [Parameter(Mandatory)][string]$ClassName,
        [Parameter(Mandatory)][string]$AssemblyFullName,
        [Parameter(Mandatory)][string]$AssemblyVersion,
        [Parameter(Mandatory)][string]$CodeBase,
        [switch]$AsActiveXControl
    )

    foreach ($root in $script:ClassRoots) {
        $clsidKey = Join-Path $root "CLSID\$Clsid"
        Set-RegValue -Path $clsidKey -Name '(default)' -Value $ClassName

        $inproc = Join-Path $clsidKey 'InprocServer32'
        # 托管 COM 类由 mscoree.dll 承载；该 DLL 在 system32 与 syswow64 各有对应位数的副本，
        # 因此两个视图都写同一个文件名即可，由宿主进程的位数决定实际加载哪一个。
        Set-RegValue -Path $inproc -Name '(default)' -Value 'mscoree.dll'
        Set-RegValue -Path $inproc -Name 'ThreadingModel' -Value 'Both'
        Set-RegValue -Path $inproc -Name 'Class' -Value $ClassName
        Set-RegValue -Path $inproc -Name 'Assembly' -Value $AssemblyFullName
        Set-RegValue -Path $inproc -Name 'RuntimeVersion' -Value $script:RuntimeVersion
        Set-RegValue -Path $inproc -Name 'CodeBase' -Value $CodeBase

        # 版本子键名必须是「程序集版本」（如 0.1.0.0），不是运行时版本。
        # CLR shim 按程序集版本查这个子键，写成 v4.0.30319 会导致激活时报
        # 0x80070002「系统找不到指定的文件」。此结构以 RegAsm /regfile 的输出为准。
        $versioned = Join-Path $inproc $AssemblyVersion
        Set-RegValue -Path $versioned -Name 'Class' -Value $ClassName
        Set-RegValue -Path $versioned -Name 'Assembly' -Value $AssemblyFullName
        Set-RegValue -Path $versioned -Name 'RuntimeVersion' -Value $script:RuntimeVersion
        Set-RegValue -Path $versioned -Name 'CodeBase' -Value $CodeBase

        Set-RegValue -Path (Join-Path $clsidKey 'ProgId') -Name '(default)' -Value $ProgId
        Set-RegValue -Path (Join-Path $clsidKey 'Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}') -Name '(default)' -Value ''

        if ($AsActiveXControl) {
            New-RegKey -Path (Join-Path $clsidKey 'Control')
            # 让宿主以不可见就绪方式创建控件，避免闪烁。
            Set-RegValue -Path (Join-Path $clsidKey 'MiscStatus') -Name '(default)' -Value '0'
            Set-RegValue -Path (Join-Path $clsidKey 'MiscStatus\1') -Name '(default)' -Value '131473'
            Set-RegValue -Path (Join-Path $clsidKey 'TypeLib') -Name '(default)' -Value $Clsid
            Set-RegValue -Path (Join-Path $clsidKey 'Version') -Name '(default)' -Value '1.0'
        }

        $progIdKey = Join-Path $root $ProgId
        Set-RegValue -Path $progIdKey -Name '(default)' -Value $ClassName
        Set-RegValue -Path (Join-Path $progIdKey 'CLSID') -Name '(default)' -Value $Clsid
    }
}

function Unregister-ComClass {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Clsid,
        [Parameter(Mandatory)][string]$ProgId
    )

    foreach ($root in $script:ClassRoots) {
        foreach ($leaf in @("CLSID\$Clsid", $ProgId)) {
            $path = Join-Path $root $leaf
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

<#
.SYNOPSIS
在宿主的加载项发现路径下登记本加载项。

.DESCRIPTION
LoadBehavior = 3 表示随宿主启动自动加载。
若加载项在启动时抛异常，宿主会把该值改成 2（已禁用），
诊断时读取此值即可判断是否被宿主禁用。
#>
function Register-ChatSheetAddIn {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$AssemblyFullName,
        [Parameter(Mandatory)][string]$AssemblyVersion,
        [Parameter(Mandatory)][string]$CodeBase
    )

    Register-ComClass -Clsid $script:AddInClsid -ProgId $script:AddInProgId `
        -ClassName $script:AddInClass -AssemblyFullName $AssemblyFullName `
        -AssemblyVersion $AssemblyVersion -CodeBase $CodeBase

    # 侧边栏控件必须注册为 ActiveX 控件，否则 CreateCTP 会失败。
    Register-ComClass -Clsid $script:TaskPaneClsid -ProgId $script:TaskPaneProgId `
        -ClassName $script:TaskPaneClass -AssemblyFullName $AssemblyFullName `
        -AssemblyVersion $AssemblyVersion -CodeBase $CodeBase `
        -AsActiveXControl

    foreach ($hive in $script:AddInHives) {
        $key = Join-Path $hive.Path $script:AddInProgId
        Set-RegValue -Path $key -Name 'FriendlyName' -Value $script:FriendlyName
        Set-RegValue -Path $key -Name 'Description' -Value $script:Description
        Set-RegValue -Path $key -Name 'LoadBehavior' -Value 3 -Type 'DWord'
        Set-RegValue -Path $key -Name 'CommandLineSafe' -Value 0 -Type 'DWord'
    }

    Clear-DisabledItems
}

<#
.SYNOPSIS
清除 Office 的加载项禁用黑名单中属于本加载项的项。

.DESCRIPTION
加载项若曾加载失败，宿主会把它写进 Resiliency\DisabledItems 并永久跳过：
此后连对象都不再创建、LoadBehavior 也不再变化，从外部看就像「压根没注册」。
不清理的话，修好根因后用户依然看不到任何变化。
值是 UTF-16 字节数组，需解码后匹配。
#>
function Clear-DisabledItems {
    [CmdletBinding()]
    param()

    $roots = @(
        'HKCU:\Software\Microsoft\Office\16.0\Excel\Resiliency\DisabledItems',
        'HKCU:\Software\Microsoft\Office\15.0\Excel\Resiliency\DisabledItems'
    )

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }

        $key = Get-Item -LiteralPath $root
        foreach ($name in $key.GetValueNames()) {
            $raw = $key.GetValue($name)
            if ($raw -isnot [byte[]]) { continue }

            $text = [System.Text.Encoding]::Unicode.GetString($raw)
            if ($text -match 'chatsheet') {
                Remove-ItemProperty -LiteralPath $root -Name $name -Force -ErrorAction SilentlyContinue
                Write-Verbose "已清除禁用黑名单项 $root\$name"
            }
        }
    }
}

function Unregister-ChatSheetAddIn {
    [CmdletBinding()]
    param()

    Unregister-ComClass -Clsid $script:AddInClsid -ProgId $script:AddInProgId
    Unregister-ComClass -Clsid $script:TaskPaneClsid -ProgId $script:TaskPaneProgId

    foreach ($hive in $script:AddInHives) {
        $key = Join-Path $hive.Path $script:AddInProgId
        if (Test-Path -LiteralPath $key) {
            Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Clear-DisabledItems
}

<#
.SYNOPSIS
读取当前注册状态，供诊断和安装后自检使用。
#>
function Get-ChatSheetRegistrationState {
    [CmdletBinding()]
    param()

    $classes = foreach ($root in $script:ClassRoots) {
        $view = if ($root -like '*Wow6432Node*') { 'x86 视图' } else { 'x64 视图' }
        foreach ($item in @(
            @{ Name = '加载项类'; Clsid = $script:AddInClsid },
            @{ Name = '侧边栏控件'; Clsid = $script:TaskPaneClsid }
        )) {
            $key = Join-Path $root "CLSID\$($item.Clsid)\InprocServer32"
            $codeBase = $null
            if (Test-Path -LiteralPath $key) {
                $codeBase = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).CodeBase
            }

            [pscustomobject]@{
                View       = $view
                Item       = $item.Name
                Registered = (Test-Path -LiteralPath $key)
                CodeBase   = $codeBase
                FileExists = if ($codeBase) { Test-Path -LiteralPath ($codeBase -replace '^file:///', '' -replace '/', '\') } else { $false }
            }
        }
    }

    $hosts = foreach ($hive in $script:AddInHives) {
        $key = Join-Path $hive.Path $script:AddInProgId
        $loadBehavior = $null
        if (Test-Path -LiteralPath $key) {
            $loadBehavior = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).LoadBehavior
        }

        [pscustomobject]@{
            Host         = $hive.Label
            Registered   = (Test-Path -LiteralPath $key)
            LoadBehavior = $loadBehavior
            # 2 表示宿主在加载失败后主动禁用了加载项，是排查的第一现场。
            Disabled     = ($loadBehavior -eq 2)
        }
    }

    [pscustomobject]@{
        Classes = $classes
        Hosts   = $hosts
    }
}

Export-ModuleMember -Function Get-ChatSheetIds, Register-ChatSheetAddIn, Unregister-ChatSheetAddIn,
    Get-ChatSheetRegistrationState, Register-ComClass, Unregister-ComClass, Clear-DisabledItems
