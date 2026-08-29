<#
.SYNOPSIS
    Verifies Miguel Downloader on a clean Windows.

.DESCRIPTION
    Written to run inside Windows Sandbox (see clean-machine-test.wsb), where the machine has
    no .NET, no FFmpeg, no yt-dlp, no Node and an untouched PATH. It can also be run on any
    freshly-imaged test machine by pointing -Setup at the installer.

    Every check reports pass or fail on its own; the script does not stop at the first failure,
    because knowing which of them broke is the point.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File clean-machine-test.ps1
#>
[CmdletBinding()]
param(
    [string] $Setup = '',
    [string] $TestUrl = 'https://www.youtube.com/watch?v=aqz-KE-bpKQ'
)

$ErrorActionPreference = 'Continue'
$results = [ordered]@{}

function Check {
    param([string] $Name, [scriptblock] $Body)

    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    try {
        $detail = & $Body
        if ($detail -is [string] -and $detail.StartsWith('FAIL')) {
            $results[$Name] = $detail
            Write-Host "    $detail" -ForegroundColor Red
        } else {
            $results[$Name] = "PASS $detail"
            Write-Host "    PASS $detail" -ForegroundColor Green
        }
    } catch {
        $results[$Name] = "FAIL $($_.Exception.Message)"
        Write-Host "    FAIL $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "Miguel Downloader - clean machine verification" -ForegroundColor White
Write-Host ("Windows {0}" -f [Environment]::OSVersion.Version)

# --- 0. Confirm the machine really is clean -------------------------------------------------
Check 'Machine has no .NET runtime installed' {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $shared = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\shared'
    if ($dotnet -or (Test-Path $shared)) {
        "FAIL - .NET is present, so this is not a clean machine (the test is still valid, but weaker)"
    } else { '- no dotnet on PATH, no shared framework' }
}

Check 'Machine has no FFmpeg or yt-dlp' {
    $found = @('ffmpeg', 'ffprobe', 'yt-dlp', 'node', 'deno') |
             Where-Object { Get-Command $_ -ErrorAction SilentlyContinue }
    if ($found) { "FAIL - already present: $($found -join ', ')" } else { '- none on PATH' }
}

# --- 1. Install ------------------------------------------------------------------------------
if (-not $Setup) {
    $Setup = (Get-ChildItem 'C:\artifacts' -Filter 'MiguelDownloaderApp-win-Setup.exe' -Recurse -ErrorAction SilentlyContinue |
              Select-Object -First 1).FullName
}
if (-not $Setup -or -not (Test-Path $Setup)) { throw "Installer not found. Pass -Setup <path>." }

Check 'Installer runs silently' {
    $p = Start-Process $Setup -ArgumentList '--silent' -Wait -PassThru
    if ($p.ExitCode -ne 0) { "FAIL - exit code $($p.ExitCode)" } else { "- exit code 0" }
}

$appRoot = Join-Path $env:LOCALAPPDATA 'MiguelDownloaderApp'
$install = Join-Path $appRoot 'current'

Check 'Application and tools are installed' {
    $missing = @('MiguelDownloader.exe', 'coreclr.dll', 'hostfxr.dll',
                 'tools\yt-dlp.exe', 'tools\ffmpeg.exe', 'tools\ffprobe.exe', 'tools\deno.exe') |
               Where-Object { -not (Test-Path (Join-Path $install $_)) }
    if ($missing) { "FAIL - missing: $($missing -join ', ')" }
    else {
        $mb = [math]::Round((Get-ChildItem $install -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
        "- everything present ($mb MB)"
    }
}

Check 'Start Menu shortcut exists' {
    $lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Miguel Downloader.lnk'
    if (Test-Path $lnk) { '- shortcut created' } else { 'FAIL - no Start Menu shortcut' }
}

# --- 2. First run ----------------------------------------------------------------------------
Check 'Application starts and is ready on first run' {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'MiguelDownloader\logs') -Recurse -Force -ErrorAction SilentlyContinue
    $proc = Start-Process (Join-Path $install 'MiguelDownloader.exe') -PassThru
    Start-Sleep -Seconds 25
    if ($proc.HasExited) { return "FAIL - exited with $($proc.ExitCode)" }

    $log = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'MiguelDownloader\logs') -Filter '*.log' |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { return 'FAIL - no log written' }

    $text = Get-Content $log.FullName -Raw
    if ($text -match '\[FTL\]') { return 'FAIL - fatal error in the log' }
    if ($text -notmatch 'Tools resolved') { return 'FAIL - tools were never resolved' }
    if ($text -notmatch 'Bundled') { return 'FAIL - tools did not resolve to the bundled copies' }

    $line = ([regex]'Tools resolved:.*').Match($text).Value
    "- $line"
}

Check 'Database was created' {
    $db = Join-Path $env:LOCALAPPDATA 'MiguelDownloader\migueldownloader.db'
    if (Test-Path $db) { "- $([math]::Round((Get-Item $db).Length / 1KB)) KB" } else { 'FAIL - no database' }
}

# --- 3. The tools actually run ---------------------------------------------------------------
Check 'yt-dlp runs and reports its version' {
    $v = & (Join-Path $install 'tools\yt-dlp.exe') --version 2>&1 | Select-Object -First 1
    if ($LASTEXITCODE -ne 0) { "FAIL - exit $LASTEXITCODE" } else { "- $v" }
}

Check 'ffmpeg runs' {
    $v = & (Join-Path $install 'tools\ffmpeg.exe') -version 2>&1 | Select-Object -First 1
    if ($LASTEXITCODE -ne 0) { "FAIL - exit $LASTEXITCODE" } else { "- $($v -replace ' Copyright.*','')" }
}

Check 'ffprobe runs' {
    $v = & (Join-Path $install 'tools\ffprobe.exe') -version 2>&1 | Select-Object -First 1
    if ($LASTEXITCODE -ne 0) { "FAIL - exit $LASTEXITCODE" } else { "- $($v -replace ' Copyright.*','')" }
}

Check 'Deno runs' {
    $v = & (Join-Path $install 'tools\deno.exe') --version 2>&1 | Select-Object -First 1
    if ($LASTEXITCODE -ne 0) { "FAIL - exit $LASTEXITCODE" } else { "- $v" }
}

# --- 4. A real download, end to end ----------------------------------------------------------
Check 'Analysis, download, mux and validation work' {
    $work = Join-Path $env:TEMP 'md-clean-test'
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    $ytdlp = Join-Path $install 'tools\yt-dlp.exe'
    $ffmpegDir = Join-Path $install 'tools'
    $deno = Join-Path $install 'tools\deno.exe'

    # Smallest audio rendition, merged and remuxed, exercising the same tools the app uses.
    & $ytdlp --no-colors --newline `
        --js-runtimes "deno:$deno" `
        --ffmpeg-location $ffmpegDir `
        -f 'worstaudio' --extract-audio --audio-format m4a `
        -o (Join-Path $work 'test.%(ext)s') `
        -- $TestUrl 2>&1 | Select-Object -Last 3 | Out-Null

    $file = Get-ChildItem $work -File | Select-Object -First 1
    if (-not $file) { return 'FAIL - no file produced' }

    $probe = & (Join-Path $install 'tools\ffprobe.exe') -v error `
        -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $file.FullName 2>&1
    if (-not $probe -or $probe -notmatch '^\d') { return "FAIL - ffprobe could not read the file" }

    "- {0} ({1:N0} bytes, {2:N0}s)" -f $file.Name, $file.Length, [double]$probe
}

# --- 5. Restart ------------------------------------------------------------------------------
Check 'Application restarts cleanly' {
    Get-Process MiguelDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    $proc = Start-Process (Join-Path $install 'MiguelDownloader.exe') -PassThru
    Start-Sleep -Seconds 20
    if ($proc.HasExited) { "FAIL - exited with $($proc.ExitCode)" } else { '- second launch OK' }
}

# --- 6. Uninstall ----------------------------------------------------------------------------
Check 'Uninstaller removes the application' {
    Get-Process MiguelDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    $updater = Join-Path $appRoot 'Update.exe'
    if (-not (Test-Path $updater)) { return 'FAIL - no Velopack updater' }

    $p = Start-Process $updater -ArgumentList '--uninstall', '--silent' -Wait -PassThru
    Start-Sleep -Seconds 8
    if (Test-Path (Join-Path $install 'MiguelDownloader.exe')) { 'FAIL - files remain' }
    elseif (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'MiguelDownloader\migueldownloader.db'))) {
        'FAIL - uninstall removed user data'
    }
    else { "- application removed and user data preserved (exit $($p.ExitCode))" }
}

# --- Summary ---------------------------------------------------------------------------------
Write-Host ''
Write-Host '================ SUMMARY ================' -ForegroundColor White
$failed = 0
foreach ($k in $results.Keys) {
    $v = $results[$k]
    $colour = if ($v.StartsWith('FAIL')) { $failed++; 'Red' } else { 'Green' }
    Write-Host ("{0,-52} {1}" -f $k, $v.Split(' ')[0]) -ForegroundColor $colour
}
Write-Host ''
Write-Host ("{0} checks, {1} failed" -f $results.Count, $failed) -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })

$report = Join-Path ([Environment]::GetFolderPath('Desktop')) 'miguel-clean-test.txt'
$results.GetEnumerator() | ForEach-Object { "{0,-52} {1}" -f $_.Key, $_.Value } |
    Set-Content $report -Encoding utf8
Write-Host "Report written to $report"
