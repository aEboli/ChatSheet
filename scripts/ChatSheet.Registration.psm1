# ChatSheet 注册表操作模块。
#
# 关键约束：加载项程序集是 AnyCPU，Microsoft Excel 可以是 x86 或 x64。
# 两种位数的 CLR 读取不同的注册表视图，因此托管 COM 类必须同时写入
# HKLM\SOFTWARE\Classes 与 HKLM\SOFTWARE\Classes\Wow6432Node；这要求安装和卸载提升权限。
# Excel/WPS 的加载项登记仍位于 HKCU，只影响执行安装的当前 Windows 用户。

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
$script:WordAddInClsid  = '{5D0F8B60-7E8F-4E57-9E19-1E0E5C2D2C31}'
$script:WordAddInProgId = 'OfficeHelper.Word.AddIn'
$script:WordAddInClass  = 'ChatWord.AddIn.WordComAddIn'
$script:WordTaskPaneProgId = 'OfficeHelper.Word.TaskPane'
$script:WordFriendlyName = 'Office-helper Word/WPS Writer 文档助手'
$script:WordDescription = '在 Word 或 WPS Writer 右侧提供文档 AI 助手，所有写操作遵循审批和读回核验。'
$script:WordAddInHives = @(
    @{ Label = 'Microsoft Word'; Path = 'HKCU:\Software\Microsoft\Office\Word\Addins' },
    # 实机探测到 WPS Writer 的启用清单：WPS\AddinsWL，不能套用 ET 表格路径。
    @{ Label = 'WPS Writer (AddinsWL)'; Path = 'HKCU:\Software\Kingsoft\Office\WPS\AddinsWL' }
)
# WPS Writer 12.8 的安装级白名单优先于当前用户清单；32 位 WPS 读取
# WOW6432Node 视图。两种视图都登记，避免安全策略只读取安装级清单时跳过加载项。
$script:WordWpsWhitelistHives = @(
    @{ Label = 'WPS Writer system whitelist (x64)'; Path = 'HKLM:\SOFTWARE\Kingsoft\Office\WPS\AddinsWL' },
    @{ Label = 'WPS Writer system whitelist (x86)'; Path = 'HKLM:\SOFTWARE\WOW6432Node\Kingsoft\Office\WPS\AddinsWL' }
)
# WPS 会把加载失败的 ProgID 留在 AddinsCL/AddinsBL，后续即使白名单和
# LoadBehavior 正确也不会再次进入 COM 的 OnConnection。安装时只清理本产品
# 自己的名称，保留其他加载项的禁用状态。
$script:WordWpsBlocklistHives = @(
    @{ Label = 'WPS Writer user blocklist (CL)'; Path = 'HKCU:\Software\Kingsoft\Office\WPS\AddinsCL' },
    @{ Label = 'WPS Writer user blocklist (BL)'; Path = 'HKCU:\Software\Kingsoft\Office\WPS\AddinsBL' },
    @{ Label = 'WPS Writer system blocklist (x64 CL)'; Path = 'HKLM:\SOFTWARE\Kingsoft\Office\WPS\AddinsCL' },
    @{ Label = 'WPS Writer system blocklist (x64 BL)'; Path = 'HKLM:\SOFTWARE\Kingsoft\Office\WPS\AddinsBL' },
    @{ Label = 'WPS Writer system blocklist (x86 CL)'; Path = 'HKLM:\SOFTWARE\WOW6432Node\Kingsoft\Office\WPS\AddinsCL' },
    @{ Label = 'WPS Writer system blocklist (x86 BL)'; Path = 'HKLM:\SOFTWARE\WOW6432Node\Kingsoft\Office\WPS\AddinsBL' }
)
$script:WordWpsBlockedNames = @(
    $script:WordAddInProgId,
    "$($script:WordAddInProgId).1"
)

# 托管 COM 类必须注册到 HKLM，不能用 HKCU。
#
# 实测依据：同一个零依赖程序集，按完全相同的键值结构注册到
# HKCU\Software\Classes 时激活报 0x80070002（系统找不到指定的文件），
# 改注册到 HKLM\SOFTWARE\Classes 后 x64 与 x86 均激活成功。
# 原因是承载托管类的 mscoree shim 不读取 HKCU 下的类注册信息。
# VSTO 能做到免提权，是因为它的原生加载器本身注册在 HKLM，
# HKCU 项只指向该加载器，并非直接指向 mscoree。
#
# 两个视图都要写：x64 Microsoft Excel 读前者，32 位 Microsoft Excel 读后者，
# 两个视图互不可见。
$script:ClassRoots = @(
    'HKLM:\SOFTWARE\Classes',
    'HKLM:\SOFTWARE\Classes\Wow6432Node'
)

# 加载项登记保留在 HKCU：宿主直接读取这些键、不经 mscoree，因此不受上述限制。
# 放在 HKCU 而非 HKLM 是有意的取舍——只影响当前用户，不改动机器上其他账户的宿主行为。
# WPS ET 使用两个历史版本路径；同时登记它们，避免首次安装依赖机器上的旧残留键。
$script:AddInHives = @(
    @{ Label = 'Microsoft Excel'; Path = 'HKCU:\Software\Microsoft\Office\Excel\Addins' },
    @{ Label = 'WPS 表格 (ET)'; Path = 'HKCU:\Software\Kingsoft\Office\ET\Addins' },
    @{ Label = 'WPS 表格 (ET 6.0)'; Path = 'HKCU:\Software\Kingsoft\Office\6.0\et\Addins' }
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
        WordAddInClsid  = $script:WordAddInClsid
        WordAddInProgId = $script:WordAddInProgId
        WordAddInClass  = $script:WordAddInClass
        WordTaskPaneProgId = $script:WordTaskPaneProgId
        ClassRoots     = $script:ClassRoots
        AddInHives     = $script:AddInHives
        WordWpsWhitelistHives = $script:WordWpsWhitelistHives
        WordWpsBlocklistHives = $script:WordWpsBlocklistHives
    }
}

function New-RegKey {
    param([Parameter(Mandatory)][string]$Path)

    if ($Path -notmatch '^(HKLM|HKCU):\\(.+)$') {
        throw "不支持的注册表路径：$Path"
    }

    $hive = if ($Matches[1] -eq 'HKLM') {
        [Microsoft.Win32.RegistryHive]::LocalMachine
    } else {
        [Microsoft.Win32.RegistryHive]::CurrentUser
    }
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        $hive,
        [Microsoft.Win32.RegistryView]::Default)
    try {
        $key = $base.CreateSubKey($Matches[2])
        if ($null -eq $key) { throw "无法创建注册表键：$Path" }
        $key.Dispose()
    }
    finally {
        $base.Dispose()
    }
}

function Set-RegValue {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()]$Value,
        [string]$Type = 'String'
    )

    if ($Path -notmatch '^(HKLM|HKCU):\\(.+)$') {
        throw "不支持的注册表路径：$Path"
    }

    $hive = if ($Matches[1] -eq 'HKLM') {
        [Microsoft.Win32.RegistryHive]::LocalMachine
    } else {
        [Microsoft.Win32.RegistryHive]::CurrentUser
    }
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        $hive,
        [Microsoft.Win32.RegistryView]::Default)
    try {
        $key = $base.CreateSubKey($Matches[2])
        if ($null -eq $key) { throw "无法创建注册表键：$Path" }
        $kind = if ($Type -eq 'DWord') {
            [Microsoft.Win32.RegistryValueKind]::DWord
        } else {
            [Microsoft.Win32.RegistryValueKind]::String
        }
        # PowerShell 注册表提供程序把默认值显示成「(default)」，但 .NET
        # RegistryKey 需要使用空字符串名称才能写入真正的默认值。
        $registryName = if ($Name -eq '(default)') { '' } else { $Name }
        $key.SetValue($registryName, $Value, $kind)
        if ($Name -eq '(default)') {
            # 清理早期版本误写入的字面量值，避免宿主读取到错误映射。
            $key.DeleteValue('(default)', $false)
        }
        $key.Dispose()
    }
    finally {
        $base.Dispose()
    }
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

function Register-ChatWordAddIn {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$AssemblyFullName,
        [Parameter(Mandatory)][string]$AssemblyVersion,
        [Parameter(Mandatory)][string]$CodeBase,
        [Parameter(Mandatory)][string]$SharedTaskPaneClsid
    )

    Register-ComClass -Clsid $script:WordAddInClsid -ProgId $script:WordAddInProgId `
        -ClassName $script:WordAddInClass -AssemblyFullName $AssemblyFullName `
        -AssemblyVersion $AssemblyVersion -CodeBase $CodeBase

    Clear-WordWpsBlocklists

    # Word 入口使用独立 ProgID，但复用已经经过验证的 WebView2 CTP 控件类。
    # 控件实际创建后由 ChatWord 注入 WordBridge，因此不会复用 Excel 工具。
    foreach ($root in $script:ClassRoots) {
        $key = Join-Path $root $script:WordTaskPaneProgId
        Set-RegValue -Path $key -Name '(default)' -Value $script:TaskPaneClass
        Set-RegValue -Path (Join-Path $key 'CLSID') -Name '(default)' -Value $SharedTaskPaneClsid
    }

    foreach ($hive in $script:WordAddInHives) {
        # WPS Writer 的 AddinsWL 是 ProgID 白名单值集合，不是 Office Addins 子键。
        if ($hive.Path -like '*\AddinsWL') {
            Set-RegValue -Path $hive.Path -Name $script:WordAddInProgId -Value ''
            $legacyKey = Join-Path $hive.Path $script:WordAddInProgId
            if (Test-Path -LiteralPath $legacyKey) { Remove-Item -LiteralPath $legacyKey -Recurse -Force -ErrorAction SilentlyContinue }
            continue
        }

        $key = Join-Path $hive.Path $script:WordAddInProgId
        Set-RegValue -Path $key -Name 'FriendlyName' -Value $script:WordFriendlyName
        Set-RegValue -Path $key -Name 'Description' -Value $script:WordDescription
        Set-RegValue -Path $key -Name 'LoadBehavior' -Value 3 -Type 'DWord'
        Set-RegValue -Path $key -Name 'CommandLineSafe' -Value 0 -Type 'DWord'
    }

    foreach ($hive in $script:WordWpsWhitelistHives) {
        Set-RegValue -Path $hive.Path -Name $script:WordAddInProgId -Value ''
        $legacyKey = Join-Path $hive.Path $script:WordAddInProgId
        if (Test-Path -LiteralPath $legacyKey) {
            Remove-Item -LiteralPath $legacyKey -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Unregister-ChatWordAddIn {
    [CmdletBinding()]
    param()

    Unregister-ComClass -Clsid $script:WordAddInClsid -ProgId $script:WordAddInProgId
    foreach ($root in $script:ClassRoots) {
        $alias = Join-Path $root $script:WordTaskPaneProgId
        if (Test-Path -LiteralPath $alias) { Remove-Item -LiteralPath $alias -Recurse -Force -ErrorAction SilentlyContinue }
    }
    foreach ($hive in $script:WordAddInHives) {
        if ($hive.Path -like '*\AddinsWL') {
            Remove-ItemProperty -LiteralPath $hive.Path -Name $script:WordAddInProgId -Force -ErrorAction SilentlyContinue
            $legacyKey = Join-Path $hive.Path $script:WordAddInProgId
            if (Test-Path -LiteralPath $legacyKey) { Remove-Item -LiteralPath $legacyKey -Recurse -Force -ErrorAction SilentlyContinue }
            continue
        }

        $key = Join-Path $hive.Path $script:WordAddInProgId
        if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue }
    }
    foreach ($hive in $script:WordWpsWhitelistHives) {
        Remove-ItemProperty -LiteralPath $hive.Path -Name $script:WordAddInProgId -Force -ErrorAction SilentlyContinue
        $legacyKey = Join-Path $hive.Path $script:WordAddInProgId
        if (Test-Path -LiteralPath $legacyKey) {
            Remove-Item -LiteralPath $legacyKey -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Clear-WordWpsBlocklists
}

function Clear-WordWpsBlocklists {
    [CmdletBinding()]
    param()

    foreach ($hive in $script:WordWpsBlocklistHives) {
        foreach ($name in $script:WordWpsBlockedNames) {
            Remove-ItemProperty -LiteralPath $hive.Path -Name $name -Force -ErrorAction SilentlyContinue
            $legacyKey = Join-Path $hive.Path $name
            if (Test-Path -LiteralPath $legacyKey) {
                Remove-Item -LiteralPath $legacyKey -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
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
        'HKCU:\Software\Microsoft\Office\15.0\Excel\Resiliency\DisabledItems',
        'HKCU:\Software\Microsoft\Office\16.0\Word\Resiliency\DisabledItems',
        'HKCU:\Software\Microsoft\Office\15.0\Word\Resiliency\DisabledItems',
        'HKCU:\Software\Kingsoft\Office\WPS\AddinsBL'
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
            @{ Name = '侧边栏控件'; Clsid = $script:TaskPaneClsid },
            @{ Name = 'Word 加载项类'; Clsid = $script:WordAddInClsid }
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

    $hostEntries = @(
        $script:AddInHives | ForEach-Object { [pscustomobject]@{ Hive = $_; ProgId = $script:AddInProgId } }
        $script:WordAddInHives | ForEach-Object { [pscustomobject]@{ Hive = $_; ProgId = $script:WordAddInProgId } }
        $script:WordWpsWhitelistHives | ForEach-Object { [pscustomobject]@{ Hive = $_; ProgId = $script:WordAddInProgId } }
    )
    $hosts = foreach ($entry in $hostEntries) {
        $hive = $entry.Hive
        $isWhitelist = $hive.Path -like '*\AddinsWL'
        $key = if ($isWhitelist) { $hive.Path } else { Join-Path $hive.Path $entry.ProgId }
        $registered = $false
        $loadBehavior = $null
        if ($isWhitelist) {
            if (Test-Path -LiteralPath $key) {
                $registered = (Get-Item -LiteralPath $key).GetValueNames() -contains $entry.ProgId
                if ($registered) { $loadBehavior = 'whitelist' }
            }
        } elseif (Test-Path -LiteralPath $key) {
            $registered = $true
            $loadBehavior = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).LoadBehavior
        }

        [pscustomobject]@{
            Host         = $hive.Label
            ProgId       = $entry.ProgId
            Registered   = $registered
            LoadBehavior = $loadBehavior
            # 2 表示宿主在加载失败后主动禁用了加载项，是排查的第一现场。
            Disabled     = ($loadBehavior -eq 2)
        }
    }

    $blocklists = foreach ($hive in $script:WordWpsBlocklistHives) {
        $blocked = @()
        if (Test-Path -LiteralPath $hive.Path) {
            $key = Get-Item -LiteralPath $hive.Path
            $blocked = @($script:WordWpsBlockedNames | Where-Object { $key.GetValueNames() -contains $_ })
        }
        [pscustomobject]@{
            Host = $hive.Label
            Path = $hive.Path
            Blocked = $blocked
        }
    }

    [pscustomobject]@{
        Classes = $classes
        Hosts   = $hosts
        Blocklists = $blocklists
    }
}

Export-ModuleMember -Function Get-ChatSheetIds, Register-ChatSheetAddIn, Unregister-ChatSheetAddIn,
    Register-ChatWordAddIn, Unregister-ChatWordAddIn,
    Get-ChatSheetRegistrationState, Register-ComClass, Unregister-ComClass, Clear-DisabledItems,
    Clear-WordWpsBlocklists
