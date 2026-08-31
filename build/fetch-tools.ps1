<#
.SYNOPSIS
    Downloads and verifies the external tools Miguel Downloader ships with.

.DESCRIPTION
    Fetches yt-dlp, FFmpeg/ffprobe and the Deno JavaScript runtime from their official
    release channels, verifies every file against the checksum the project publishes, and
    stages them into a folder the installer and the portable package embed.

    Each project publishes checksums in a different shape, which is exactly the kind of
    detail that fails silently if assumed:

      yt-dlp          SHA2-256SUMS      "<hash>  <filename>"      (coreutils style)
      FFmpeg-Builds   checksums.sha256  "<hash>  <filename>"      (coreutils style)
      Deno            .sha256sum        PowerShell Get-FileHash   ("Hash : <HASH>")

    A mismatch aborts. An unverified executable is not worth shipping.

    Writes tools/tools.json recording what was staged, which the application reads to
    report versions and to verify its own installation.

.EXAMPLE
    pwsh build/fetch-tools.ps1

.EXAMPLE
    pwsh build/fetch-tools.ps1 -Force        # re-download even if already staged
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = '',
    [string] $ToolLockPath = '',
    [switch] $Force,
    [switch] $Offline
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'build/tools-cache' }
$lockPath = if ($ToolLockPath) {
    if ([IO.Path]::IsPathRooted($ToolLockPath)) { [IO.Path]::GetFullPath($ToolLockPath) }
    else { [IO.Path]::GetFullPath((Join-Path $repoRoot $ToolLockPath)) }
} else {
    Join-Path $PSScriptRoot 'tools.lock.json'
}
$repoBoundary = [IO.Path]::GetFullPath($repoRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $lockPath.StartsWith($repoBoundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a tool lock outside the repository: $lockPath"
}
if (-not (Test-Path -LiteralPath $lockPath)) { throw "Tool lock was not found at $lockPath" }
$toolLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($toolLock.schemaVersion -ne 1) { throw "Unsupported tool lock schema: $($toolLock.schemaVersion)" }

$temp = Join-Path ([IO.Path]::GetTempPath()) ("md-tools-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $OutputDirectory, $temp | Out-Null

function Write-Step { param([string] $Text) Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "    $Text" -ForegroundColor Green }

function Get-Json {
    param([string] $Uri)
    # GitHub rejects requests that do not identify themselves.
    Invoke-RestMethod -Uri $Uri -Headers @{ 'User-Agent' = 'MiguelDownloader-Build' } -TimeoutSec 60
}

function Get-AssetUrl {
    param($Release, [string] $Name)
    $asset = $Release.assets | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $asset) { throw "Release '$($Release.tag_name)' does not publish an asset named '$Name'." }
    return $asset.browser_download_url
}

function Save-File {
    param([string] $Uri, [string] $Path)
    if (-not $Uri.StartsWith('https://')) { throw "Refusing to download over a non-HTTPS URL: $Uri" }
    Invoke-WebRequest -Uri $Uri -OutFile $Path -Headers @{ 'User-Agent' = 'MiguelDownloader-Build' } -TimeoutSec 600
}

<#
    Reads an expected hash out of a checksum document, accepting the formats the upstream
    projects actually publish rather than assuming one of them.
#>
function Read-ExpectedHash {
    param([string] $Document, [string] $FileName)

    foreach ($line in ($Document -split "`n")) {
        $trimmed = $line.Trim()
        if (-not $trimmed) { continue }

        # PowerShell Get-FileHash output: "Hash      : ABC123..."
        if ($trimmed -match '^Hash\s*:\s*([0-9a-fA-F]{64})$') { return $Matches[1].ToLowerInvariant() }

        # coreutils: "<hash>  <name>" (the '*' marks binary mode)
        if ($trimmed -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
            if ($Matches[2].Trim() -ieq $FileName) { return $Matches[1].ToLowerInvariant() }
        }
    }

    # A single bare hash with no file name is also seen in the wild.
    if ($Document.Trim() -match '^([0-9a-fA-F]{64})$') { return $Matches[1].ToLowerInvariant() }

    throw "Could not find a SHA-256 for '$FileName' in the published checksum document."
}

function Assert-Hash {
    param([string] $Path, [string] $Expected, [string] $Label)

    $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "Checksum mismatch for ${Label}. Expected $Expected, got $actual. Refusing to stage it."
    }
    Write-Ok "verified $Label"
    return $actual
}

$manifest = [ordered]@{ lockSchemaVersion = 1; tools = [ordered]@{} }

try {
    # ---------------------------------------------------------------- yt-dlp (Unlicense)
    Write-Step 'yt-dlp'
    $yt = $toolLock.tools.ytDlp
    $ytTarget  = Join-Path $OutputDirectory 'yt-dlp.exe'

    if ($Force -or -not (Test-Path $ytTarget)) {
        if ($Offline) { throw 'yt-dlp is not cached and offline mode forbids downloading it.' }
        $ytRelease = Get-Json $yt.releaseApi
        $exe  = Join-Path $temp 'yt-dlp.exe'
        $sums = Join-Path $temp 'SHA2-256SUMS'
        Save-File (Get-AssetUrl $ytRelease $yt.asset)          $exe
        Save-File (Get-AssetUrl $ytRelease $yt.checksumsAsset) $sums

        $published = Read-ExpectedHash (Get-Content $sums -Raw) $yt.asset
        if ($published -ne $yt.assetSha256) { throw "The published yt-dlp checksum changed from the lock file." }
        [void](Assert-Hash $exe $yt.assetSha256 $yt.asset)
        Move-Item $exe $ytTarget -Force
    } else {
        Write-Ok 'already staged'
    }
    $manifest.tools['yt-dlp'] = [ordered]@{ version = $yt.version; file = 'yt-dlp.exe'; sha256 = $toolLock.files.'yt-dlp.exe'; license = $yt.license }

    # -------------------------------------------------------------- FFmpeg / ffprobe
    Write-Step 'FFmpeg and ffprobe'
    $ff = $toolLock.tools.ffmpeg
    $ffTarget = Join-Path $OutputDirectory 'ffmpeg.exe'

    if ($Force -or -not (Test-Path $ffTarget)) {
        if ($Offline) { throw 'FFmpeg is not cached and offline mode forbids downloading it.' }
        # The selected lock decides the upstream and licence profile. The website package uses
        # yt-dlp's GPL build; the Microsoft Store package uses BtbN's LGPL build so Store terms do
        # not conflict with the bundled media tools.
        $ffRelease = Get-Json $ff.releaseApi
        $assetName = $ff.asset

        $zip  = Join-Path $temp $assetName
        $sums = Join-Path $temp 'checksums.sha256'
        Save-File (Get-AssetUrl $ffRelease $assetName)       $zip
        Save-File (Get-AssetUrl $ffRelease $ff.checksumsAsset) $sums

        $published = Read-ExpectedHash (Get-Content $sums -Raw) $assetName
        if ($published -ne $ff.assetSha256) { throw "The published FFmpeg checksum changed from the lock file." }
        [void](Assert-Hash $zip $ff.assetSha256 $assetName)

        $extract = Join-Path $temp 'ffmpeg'
        Expand-Archive -Path $zip -DestinationPath $extract -Force

        # The archive nests everything under a versioned folder; only bin/ is needed.
        $bin = Get-ChildItem $extract -Directory -Recurse |
               Where-Object { $_.Name -eq 'bin' } | Select-Object -First 1
        if (-not $bin) { throw 'The FFmpeg archive did not contain a bin folder.' }

        foreach ($file in Get-ChildItem $bin.FullName -File) {
            # ffplay is a media player; the application never invokes it.
            if ($file.Name -ieq 'ffplay.exe') { continue }
            Copy-Item $file.FullName (Join-Path $OutputDirectory $file.Name) -Force
        }

        # Both GPL and LGPL require a clear licence/source notice. Shared LGPL libraries stay next
        # to the executables, so a recipient can replace them without modifying the application.
        $licenseDir = Join-Path $OutputDirectory 'licenses'
        New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
        @"
FFmpeg
------
The ffmpeg.exe, ffprobe.exe and shared libraries shipped alongside Miguel Downloader are
unmodified binaries built and published by $($ff.provider), licensed under the
$($ff.licenseName).

Release      : $($ff.version)
Asset        : $assetName
SHA-256      : $($ff.assetSha256)
Binaries     : $($ff.binariesUrl)
Build source : $($ff.sourceUrl)
FFmpeg source: $($ff.ffmpegSourceUrl)
License text : $($ff.licenseUrl)

Miguel Downloader runs these as separate processes and does not link against them.
"@ | Set-Content (Join-Path $licenseDir 'FFmpeg.txt') -Encoding utf8

        $upstreamLicense = Get-ChildItem -LiteralPath $extract -Filter 'LICENSE.txt' -File -Recurse |
                           Select-Object -First 1
        if ($upstreamLicense) {
            Copy-Item -LiteralPath $upstreamLicense.FullName `
                -Destination (Join-Path $licenseDir 'FFmpeg-License.txt') -Force
        }

    } else {
        Write-Ok 'already staged'
    }
    $manifest.tools['ffmpeg'] = [ordered]@{ version = $ff.version; file = 'ffmpeg.exe'; sha256 = $toolLock.files.'ffmpeg.exe'; license = $ff.license }

    # -------------------------------------------------------------------- Deno (MIT)
    Write-Step 'Deno JavaScript runtime'
    $deno = $toolLock.tools.deno
    $denoTarget = Join-Path $OutputDirectory 'deno.exe'

    if ($Force -or -not (Test-Path $denoTarget)) {
        if ($Offline) { throw 'Deno is not cached and offline mode forbids downloading it.' }
        $denoRelease = Get-Json $deno.releaseApi
        $assetName = $deno.asset

        $zip  = Join-Path $temp $assetName
        $sums = Join-Path $temp 'deno.sha256sum'
        Save-File (Get-AssetUrl $denoRelease $assetName)                 $zip
        Save-File (Get-AssetUrl $denoRelease $deno.checksumsAsset)       $sums

        # Deno publishes PowerShell Get-FileHash output here, not coreutils format.
        $published = Read-ExpectedHash (Get-Content $sums -Raw) $assetName
        if ($published -ne $deno.assetSha256) { throw "The published Deno checksum changed from the lock file." }
        [void](Assert-Hash $zip $deno.assetSha256 $assetName)

        $extract = Join-Path $temp 'deno'
        Expand-Archive -Path $zip -DestinationPath $extract -Force
        Copy-Item (Join-Path $extract 'deno.exe') $denoTarget -Force

    } else {
        Write-Ok 'already staged'
    }
    $manifest.tools['deno'] = [ordered]@{ version = $deno.version; file = 'deno.exe'; sha256 = $toolLock.files.'deno.exe'; license = $deno.license }

    # Verify every executable and shared library even when the cache was reused. This closes the
    # gap where a stale or modified local cache could otherwise bypass the archive verification.
    Write-Step 'Locked staged files'
    foreach ($lockedFile in $toolLock.files.PSObject.Properties) {
        $path = Join-Path $OutputDirectory $lockedFile.Name
        if (-not (Test-Path -LiteralPath $path)) { throw "The locked tool file is missing: $($lockedFile.Name)" }
        [void](Assert-Hash $path ([string]$lockedFile.Value) $lockedFile.Name)
    }

    # ------------------------------------------------------------------------ manifest
    $manifestPath = Join-Path $OutputDirectory 'tools.json'
    # Windows PowerShell writes a BOM with -Encoding utf8, which some JSON readers choke on.
    # Writing the bytes directly keeps the manifest plain UTF-8.
    $json = $manifest | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($manifestPath, $json, (New-Object Text.UTF8Encoding $false))

    # Treat the cache as untrusted when it is reused. Only locked payload files plus the manifest
    # and the two FFmpeg notice files may leave this directory for an installer or MSIX. This also
    # rejects junctions/symlinks that could make a later recursive copy escape the verified tree.
    Write-Step 'Staged file allowlist'
    $allowedFiles = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($lockedFile in $toolLock.files.PSObject.Properties) {
        $name = [string]$lockedFile.Name
        if ([IO.Path]::IsPathRooted($name) -or [IO.Path]::GetFileName($name) -ne $name) {
            throw "Locked tool file name must be a root-level file: $name"
        }
        [void]$allowedFiles.Add($name)
    }
    [void]$allowedFiles.Add('tools.json')
    [void]$allowedFiles.Add('licenses\FFmpeg.txt')
    [void]$allowedFiles.Add('licenses\FFmpeg-License.txt')

    $outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) +
                  [IO.Path]::DirectorySeparatorChar
    foreach ($entry in Get-ChildItem -LiteralPath $OutputDirectory -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A reparse point is not allowed in the staged tools: $($entry.FullName)"
        }
        if ($entry.PSIsContainer) { continue }

        $fullPath = [IO.Path]::GetFullPath($entry.FullName)
        if (-not $fullPath.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "A staged tool resolved outside the cache: $fullPath"
        }
        $relative = $fullPath.Substring($outputRoot.Length)
        if (-not $allowedFiles.Contains($relative)) {
            throw "Unexpected file in staged tools: $relative"
        }
    }

    Write-Host ''
    Write-Host "Staged into $OutputDirectory" -ForegroundColor Green
    Get-ChildItem $OutputDirectory -File | Sort-Object Length -Descending |
        Select-Object -First 8 |
        ForEach-Object { Write-Host ("  {0,-28} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

    $total = (Get-ChildItem $OutputDirectory -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("  {0,-28} {1,8:N1} MB" -f '(total)', ($total / 1MB)) -ForegroundColor Green
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
