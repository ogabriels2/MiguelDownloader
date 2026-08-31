<#
.SYNOPSIS
    Captures the running application window at the Microsoft Store desktop minimum size.

.DESCRIPTION
    Temporarily resizes the Miguel Downloader window to 1366x768, asks Windows to render that
    window directly into a PNG, and restores its original position and size. PrintWindow avoids
    including the taskbar, notifications, other windows or desktop data in the Store image.
#>
[CmdletBinding()]
param(
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'artifacts/store/listing/miguel-downloader-home-1366x768.png'
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class StoreScreenshotNativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(
        IntPtr handle, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
'@

$process = Get-Process MiguelDownloader -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
    Sort-Object StartTime -Descending |
    Select-Object -First 1

if (-not $process) {
    throw 'Miguel Downloader is not running with a visible main window.'
}

$handle = $process.MainWindowHandle
$original = New-Object StoreScreenshotNativeMethods+RECT
if (-not [StoreScreenshotNativeMethods]::GetWindowRect($handle, [ref]$original)) {
    throw 'Windows did not return the application window bounds.'
}

$originalWidth = $original.Right - $original.Left
$originalHeight = $original.Bottom - $original.Top
$width = 1366
$height = 768

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$previousClipboard = $null
try {
    $previousClipboard = [Windows.Forms.Clipboard]::GetDataObject()
}
catch [Runtime.InteropServices.ExternalException] {
    # Another desktop process can hold the clipboard briefly. Capturing must still work; in this
    # rare case there simply is no safe snapshot to restore afterwards.
}

try {
    # HWND_TOPMOST makes the app cover the taskbar for this one frame. Unlike hiding the taskbar,
    # it does not alter Explorer or any system setting and is reversed in the finally block.
    $topMost = [IntPtr](-1)
    $notTopMost = [IntPtr](-2)
    $showWindow = [uint32]0x0040
    [StoreScreenshotNativeMethods]::SetWindowPos(
        $handle, $topMost, 0, 0, $width, $height, $showWindow) | Out-Null
    $hasFocus = $false
    for ($attempt = 0; $attempt -lt 10 -and -not $hasFocus; $attempt++) {
        [StoreScreenshotNativeMethods]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Milliseconds 200
        $hasFocus = [StoreScreenshotNativeMethods]::GetForegroundWindow() -eq $handle
    }
    if (-not $hasFocus) {
        throw 'Miguel Downloader did not become the foreground window; capture was aborted.'
    }
    Start-Sleep -Milliseconds 600

    # Alt+PrintScreen asks Desktop Window Manager for the composed active-window frame. This is
    # the reliable path for WPF windows with Mica, where PrintWindow returns an empty surface.
    $keyUp = [uint32]2
    [StoreScreenshotNativeMethods]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [StoreScreenshotNativeMethods]::keybd_event(0x2C, 0, 0, [UIntPtr]::Zero)
    [StoreScreenshotNativeMethods]::keybd_event(0x2C, 0, $keyUp, [UIntPtr]::Zero)
    [StoreScreenshotNativeMethods]::keybd_event(0x12, 0, $keyUp, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 800

    $bitmap = $null
    for ($attempt = 0; $attempt -lt 10 -and -not $bitmap; $attempt++) {
        try { $bitmap = [Windows.Forms.Clipboard]::GetImage() }
        catch [Runtime.InteropServices.ExternalException] { Start-Sleep -Milliseconds 150 }
    }
    if (-not $bitmap) { throw 'Windows did not place the active-window capture on the clipboard.' }
    if ($bitmap.Width -lt $width -or $bitmap.Height -lt $height) {
        throw "The captured window is $($bitmap.Width)x$($bitmap.Height); expected at least ${width}x${height}."
    }

    $bitmap.Save($OutputPath, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}
finally {
    [StoreScreenshotNativeMethods]::SetWindowPos(
        $handle, $notTopMost, $original.Left, $original.Top,
        $originalWidth, $originalHeight, $showWindow) | Out-Null

    # The screenshot shortcut temporarily replaces the clipboard. Restore the user's prior data
    # without inspecting or logging any of its formats or contents.
    if ($previousClipboard) {
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            try {
                [Windows.Forms.Clipboard]::SetDataObject($previousClipboard, $true)
                break
            }
            catch [Runtime.InteropServices.ExternalException] {
                if ($attempt -lt 9) { Start-Sleep -Milliseconds 150 }
            }
        }
    }
}

$file = Get-Item -LiteralPath $OutputPath
"{0}  {1}x{2}  {3:N0} bytes" -f $file.FullName, $width, $height, $file.Length
