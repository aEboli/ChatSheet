# 截取 Excel 主窗口，用于目视确认面板渲染是否正常。
# 日志只能证明消息桥连通，无法证明界面布局正确。
param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinCap
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
'@

$proc = Get-Process -Name EXCEL -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object -First 1

if (-not $proc) { throw '未找到带主窗口的 Excel 进程。' }

$hwnd = $proc.MainWindowHandle

# 3 = SW_MAXIMIZE，确保窗口足够大以完整显示侧边栏。
[void][WinCap]::ShowWindow($hwnd, 3)
[void][WinCap]::SetForegroundWindow($hwnd)
Start-Sleep -Seconds 2

$rect = New-Object WinCap+RECT
if (-not [WinCap]::GetWindowRect($hwnd, [ref]$rect)) { throw '取窗口矩形失败。' }

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { throw "窗口尺寸异常：$width x $height" }

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)

if (-not $OutputPath) {
    $OutputPath = Join-Path $env:TEMP 'chatsheet-panel.png'
}

$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()

Write-Output "已保存截图：$OutputPath（${width}x${height}）"
