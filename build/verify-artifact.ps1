<#
.SYNOPSIS
    Acceptance test for a built artifact: drives the real interface and fails on anything
    the application logs as an error.

.DESCRIPTION
    Unit tests cannot see a XAML fault. A style whose BasedOn is keyed for an incompatible
    target type compiles, publishes and installs happily, then throws on the UI thread the
    first time the list realises a container -- so the page the user watches downloads on
    comes up broken while every unit test still passes. This script is what catches that
    class of defect: it opens each page, performs a real analysis and a real download, and
    then asserts the log for that run contains no error at all.

    Run it against whatever is being shipped, not against a development build.

.EXAMPLE
    pwsh build/verify-artifact.ps1
    Tests the Velopack per-user installation in %LOCALAPPDATA%\MiguelDownloaderApp\current.

.EXAMPLE
    pwsh build/verify-artifact.ps1 -AppPath D:\portable\MiguelDownloader.exe
    Tests an unpacked portable build.
#>
[CmdletBinding()]
param(
    [string] $AppPath = "$env:LOCALAPPDATA\MiguelDownloaderApp\current\MiguelDownloader.exe",
    [string] $Url     = 'https://www.youtube.com/watch?v=jNQXAC9IVRw',
    [int]    $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$script:Failures = 0
function Step($t) { Write-Host "`n=== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "    PASS  $t" -ForegroundColor Green }
function Bad($t)  { Write-Host "    FAIL  $t" -ForegroundColor Red; $script:Failures++ }

if (-not (Test-Path $AppPath)) { throw "No application at $AppPath" }
$appDir  = Split-Path $AppPath -Parent
$logDir  = "$env:LOCALAPPDATA\MiguelDownloader\logs"
$outDir  = Join-Path $env:USERPROFILE 'Downloads\Miguel Downloader'
$ffprobe = Join-Path $appDir 'tools\ffprobe.exe'
if (-not (Test-Path $ffprobe)) {
    # A development build has no bundled tools folder; fall back to the build cache so the
    # produced file is still validated rather than merely counted.
    $cache = Join-Path (Split-Path $PSScriptRoot -Parent) 'build\tools-cache\ffprobe.exe'
    if (Test-Path $cache) { $ffprobe = $cache }
}

Write-Host "Verifying $AppPath" -ForegroundColor Green

Get-Process MiguelDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2

# Everything already on disk is invisible to this run, so only what we cause counts.
$seen = @{}
if (Test-Path $outDir) { Get-ChildItem $outDir -File -Recurse | ForEach-Object { $seen[$_.FullName] = $true } }
$logMark = if (Test-Path $logDir) {
    $l = Get-ChildItem $logDir -Filter '*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($l) { (Get-Item $l.FullName).Length } else { 0 }
} else { 0 }

Step 'Startup'
$app = Start-Process $AppPath -PassThru
function Stop-App {
    if ($app -and -not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(15000)) { $app.Kill() }
    }
}
$root = $null
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Milliseconds 500
    $app.Refresh()
    if ($app.MainWindowHandle -ne 0) {
        $root = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
        if ($root) { break }
    }
}
if (-not $root) { Bad 'the main window never appeared'; Stop-App; exit 1 }
Ok "window '$($root.Current.Name)' after $([int]($i * 0.5))s"

function Find-Element([Windows.Automation.Condition] $condition, [int] $timeout = 20) {
    for ($t = 0; $t -lt $timeout * 2; $t++) {
        $e = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
function ByName([string] $name, [int] $timeout = 20) {
    Find-Element (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $name)) $timeout
}
function ById([string] $id, [int] $timeout = 20) {
    Find-Element (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $id)) $timeout
}
function Invoke-Element($e) {
    $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# Showing every surface is the point: one that throws while realising its containers only fails
# once it is actually displayed.
#
# The tabs are located through the panel rather than by their labels. Searching by label would
# match the TextBlock inside the header, which carries neither pattern, and would put accented
# Portuguese in this file -- which Windows PowerShell reads as ANSI unless the file carries a
# byte order mark, so the names would not match anyway.
# On a machine that has never run this application, the quick guide opens over the window and is
# modal, so everything after this would time out waiting for controls behind it. Dismissing it is
# also the check that it can be dismissed: a first-run dialog with no way out would be worse than
# no dialog at all.
Step 'First-run guide'
$guide = ByName 'Guia rapido' 6
if (-not $guide) {
    # ByName cannot carry the accented title from this file, so fall back to the window list.
    $guide = Find-Element (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Window)) 3
}
if ($guide -and $guide.Current.Name -like 'Guia*') {
    $skip = $guide.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button)))
    if ($skip) {
        Invoke-Element $skip
        Start-Sleep -Milliseconds 800
        Ok 'the guide opened and closed'
    }
    else { Bad 'the guide opened with no button to dismiss it' }
}
else { Ok 'no guide (already seen on this machine)' }
Step 'Opening the activity tabs'
$tabs = ById 'ActivityTabs' 20
if (-not $tabs) { Bad 'the activity panel was not found' }
else {
    $items = $tabs.FindAll([Windows.Automation.TreeScope]::Children,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::TabItem)))
    if ($items.Count -lt 2) { Bad "expected the queue and history tabs, found $($items.Count)" }
    foreach ($item in $items) {
        $label = $item.Current.Name
        # A tab whose header is a panel reports its type name here unless someone set an
        # automation name, and a screen reader would read that type name aloud.
        if (-not $label -or $label -like 'System.Windows.*') {
            Bad "a tab has no accessible name (reads as '$label')"
        }
        try {
            $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 900
            Ok "opened $label"
        } catch {
            Bad "could not open '$label': $($_.Exception.Message)"
        }
    }
    # Leave the queue tab selected so a started download is visible.
    try { $items[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { }
    Start-Sleep -Milliseconds 600
}

# The menu bar and the status bar are the two surfaces a Windows application is judged by, and
# both are easy to break without noticing: a menu item bound to a command that no longer exists
# still renders, it just does nothing. Every top-level menu must be present and openable.
Step 'Checking the menu bar'
$menu = ById 'MainMenu' 20
if (-not $menu) { Bad 'the menu bar was not found' }
else {
    $expected = @('Arquivo', 'Editar', 'Exibir', 'Ferramentas', 'Ajuda')
    $items = $menu.FindAll([Windows.Automation.TreeScope]::Children,
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::MenuItem)))
    $found = @($items | ForEach-Object { $_.Current.Name })
    foreach ($name in $expected) {
        if ($found -contains $name) { Ok "menu '$name'" }
        else { Bad "the '$name' menu is missing (found: $($found -join ', '))" }
    }
}

# The status bar reports what the queue is doing. An empty readout means the timer that feeds it
# stopped, which is silent from the user's side until they wonder why nothing moves.
Step 'Checking the status bar'
$statusText = ById 'StatusText' 15
if (-not $statusText) { Bad 'the status bar was not found' }
elseif (-not $statusText.Current.Name) { Bad 'the status bar is blank' }
else { Ok 'the status bar is reporting' }
if (ById 'StatusFreeSpace' 5) { Ok 'free space is shown' } else { Bad 'free space is not shown' }

# F1 is the shortcut reference. It is opened here rather than through the menu because an owned
# window does not appear under the desktop in the automation tree, so it is checked by asking the
# process what it has on screen.
Step 'Opening the keyboard shortcuts (F1)'
$before = @($app.MainWindowHandle)
[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle).SetFocus()
Start-Sleep -Milliseconds 400
[Windows.Forms.SendKeys]::SendWait('{F1}')
Start-Sleep 2
$help = ByName 'Atalhos de teclado' 8
if ($help) { Ok 'the shortcut reference opened'; $help.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() }
else { Bad 'F1 did not open the shortcut reference' }
Start-Sleep -Milliseconds 600
Step 'Analysing a real URL'
$box = ById 'UrlBox' 20
if (-not $box) { Bad 'the URL box was not found'; Stop-App; exit 1 }
$box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Url)
$analyse = ByName 'Analisar' 10
if (-not $analyse) { Bad 'the Analyse button was not found'; Stop-App; exit 1 }
Invoke-Element $analyse

$download = $null
for ($t = 0; $t -lt 180; $t++) {
    Start-Sleep 1
    $d = ByName 'Baixar' 1
    if ($d -and $d.Current.IsEnabled) { $download = $d; break }
}
if (-not $download) { Bad 'the analysis never produced a downloadable result'; Stop-App; exit 1 }
Ok "analysed in ~${t}s"

Step 'Downloading'
Invoke-Element $download
$produced = @()
for ($t = 0; $t -lt $TimeoutSeconds; $t++) {
    Start-Sleep 1
    if (-not (Test-Path $outDir)) { continue }
    $new = Get-ChildItem $outDir -File -Recurse |
        Where-Object { -not $seen.ContainsKey($_.FullName) -and $_.Extension -notin '.part', '.ytdl', '.tmp' }
    if ($new) { Start-Sleep 3; $produced = @(Get-ChildItem $outDir -File -Recurse |
        Where-Object { -not $seen.ContainsKey($_.FullName) -and $_.Extension -notin '.part', '.ytdl', '.tmp' }); break }
}
if (-not $produced) { Bad "nothing was produced within ${TimeoutSeconds}s" }
else {
    Ok "downloaded in ~${t}s"
    foreach ($f in $produced) {
        Write-Host ("      {0} ({1:N0} bytes)" -f $f.Name, $f.Length) -ForegroundColor DarkGray
        if ($f.Length -le 0) { Bad "$($f.Name) is empty" }
        # A file that exists proves nothing; it has to decode.
        if (Test-Path $ffprobe) {
            $probe = & $ffprobe -v error -show_entries 'format=duration' -of csv=p=0 -- $f.FullName 2>&1
            if ($LASTEXITCODE -ne 0) { Bad "ffprobe rejected $($f.Name): $probe" }
            elseif ([double]$probe -le 0) { Bad "$($f.Name) has no duration" }
            else { Ok "$($f.Name) decodes, duration ${probe}s" }
        }
    }
}

Step 'Shutdown'
$app.CloseMainWindow() | Out-Null
if (-not $app.WaitForExit(30000)) { $app.Kill(); Bad 'the window closed but the process did not exit within 30s' }
else { Ok "exited with code $($app.ExitCode)" }

Step 'What the application logged for this run'
$log = Get-ChildItem $logDir -Filter '*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $log) { Bad 'no log was written' }
else {
    $text = [IO.File]::ReadAllText($log.FullName)
    if ($text.Length -gt $logMark) { $text = $text.Substring($logMark) }
    $bad = $text -split "`n" | Where-Object { $_ -match '\[ERR\]|\[FTL\]' }
    if ($bad) {
        Bad "$($bad.Count) error line(s) logged:"
        $bad | Select-Object -First 10 | ForEach-Object { Write-Host "      $($_.Trim())" -ForegroundColor Red }
    } else { Ok 'no errors logged' }
}

Write-Host ''
if ($script:Failures -gt 0) { Write-Host "$script:Failures check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host 'Artifact verified.' -ForegroundColor Green
