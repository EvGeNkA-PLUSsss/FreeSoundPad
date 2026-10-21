param(
    [string]$Out = "docs/screenshots/sounds.png",
    [switch]$BrowserTab,
    [switch]$Settings,
    [switch]$Screen
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class FspNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

[FspNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$process = Get-Process FreeSoundPad -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "FreeSoundPad is not running" }

$handle = $process.MainWindowHandle
[FspNative]::ShowWindow($handle, 9) | Out-Null
[FspNative]::SetWindowPos($handle, [IntPtr](-1), 0, 0, 0, 0, 0x0043) | Out-Null
[FspNative]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 900

[FspNative+RECT]$rect = New-Object FspNative+RECT
[FspNative]::GetWindowRect($handle, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
Write-Output "window: $($rect.Left),$($rect.Top) ${width}x${height}"

function Invoke-Click([int]$x, [int]$y) {
    [FspNative]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    [FspNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [FspNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 800
}

if ($BrowserTab) {
    Invoke-Click ($rect.Left + [int]($width / 2) + 58) ($rect.Top + 28)
    Start-Sleep -Seconds 2
}

if ($Settings) {
    Invoke-Click ($rect.Left + $width - 176) ($rect.Top + 28)
    Start-Sleep -Seconds 1
}

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

if ($Screen) {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $mode = "screen"
} else {
    $hdc = $graphics.GetHdc()
    $ok = [FspNative]::PrintWindow($handle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    $mode = "printwindow=$ok"
    if (-not $ok) {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $mode = "fallback-screen"
    }
}

$graphics.Dispose()

$target = Join-Path (Get-Location) $Out
$dir = Split-Path $target
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
[FspNative]::SetWindowPos($handle, [IntPtr](-2), 0, 0, 0, 0, 0x0043) | Out-Null
Write-Output "saved: $target ($mode)"
