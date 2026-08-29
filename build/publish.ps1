<#
.SYNOPSIS
    Tests, publishes and packages Miguel Downloader with Velopack.

.DESCRIPTION
    Produces a self-contained Windows installer, portable bundle, full update package and release
    feed. Installed and portable copies use that feed for transactional automatic updates.
#>
[CmdletBinding()]
param(
    [switch] $Portable,
    [switch] $Installer,
    [string] $Version = '',
    [string] $Runtime = 'win-x64',
    [string] $ReleaseNotes = '',
    [switch] $SkipTests,
    [switch] $SkipToolFetch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/MiguelDownloader.App/MiguelDownloader.App.csproj'
$artifacts = Join-Path $repoRoot 'artifacts'
$releases = Join-Path $artifacts 'releases'
$stage = Join-Path $artifacts 'stage-velopack'
$toolsCache = Join-Path $repoRoot 'build/tools-cache'
$icon = Join-Path $repoRoot 'src/MiguelDownloader.App/Assets/migueldownloader.ico'
$licence = Join-Path $repoRoot 'legal/LICENCA.txt'
$packId = 'MiguelDownloaderApp'
$channel = 'win'

if (-not $Portable -and -not $Installer) { $Portable = $true; $Installer = $true }

function Write-Step { param([string] $Text) Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok { param([string] $Text) Write-Host "    $Text" -ForegroundColor Green }

function Invoke-Native {
    param([string] $Name, [scriptblock] $Body)
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

function Get-ProjectVersion {
    if ($Version) { return $Version }
    $props = [xml](Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props'))
    $found = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $found) { throw 'Directory.Build.props does not define <Version>.' }
    return [string]$found
}

$resolvedVersion = Get-ProjectVersion
if ($resolvedVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw "Version '$resolvedVersion' is not SemVer 2.0."
}
$numericVersion = ([regex]::Match($resolvedVersion, '^\d+\.\d+\.\d+')).Value + '.0'

if (-not $ReleaseNotes) {
    $ReleaseNotes = Join-Path $repoRoot 'docs/release-notes.md'
}
if (-not (Test-Path -LiteralPath $ReleaseNotes)) {
    throw "Release notes were not found at $ReleaseNotes."
}

Write-Host "Miguel Downloader $resolvedVersion ($Runtime)" -ForegroundColor Green
New-Item -ItemType Directory -Force -Path $artifacts, $releases | Out-Null

# A failed or interrupted local build may have already written this exact version. Velopack
# correctly refuses to overwrite a release, so clear only that version and its generated channel
# indexes. Packages from earlier versions remain available for delta generation.
$sameVersionAssets = @(Get-ChildItem -LiteralPath $releases -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "$packId-$resolvedVersion-*" })
if ($sameVersionAssets.Count -gt 0) {
    $generatedNames = @(
        "$packId-$channel-Setup.exe",
        "$packId-$channel-Portable.zip",
        'releases.win.json',
        'assets.win.json',
        'RELEASES'
    )
    @($sameVersionAssets) + @(
        Get-ChildItem -LiteralPath $releases -File |
            Where-Object { $_.Name -in $generatedNames }
    ) | Sort-Object FullName -Unique | Remove-Item -Force
}

Write-Step 'Pinned build tools'
Invoke-Native 'dotnet tool restore' { dotnet tool restore }

if (-not $SkipTests) {
    Write-Step 'Automated tests'
    Invoke-Native 'dotnet test' {
        dotnet test (Join-Path $repoRoot 'tests/MiguelDownloader.Tests') `
            --configuration Release `
            --filter 'Category!=Integration&Category!=ToolInstall' `
            --nologo -v minimal
    }
}

Write-Step 'Verified external tools'
& (Join-Path $PSScriptRoot 'fetch-tools.ps1') -OutputDirectory $toolsCache -Offline:$SkipToolFetch
if ($LASTEXITCODE -ne 0) { throw "Tool verification failed with exit code $LASTEXITCODE" }

foreach ($required in @('yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe', 'deno.exe', 'tools.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $toolsCache $required))) {
        throw "The verified tools cache is missing $required."
    }
}

if (Test-Path -LiteralPath $stage) {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifacts) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a staging path outside artifacts: $resolvedStage"
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}

Write-Step 'Self-contained application'
Invoke-Native 'dotnet publish' {
    dotnet publish $project `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        --output $stage `
        -p:Version=$resolvedVersion `
        -p:FileVersion=$numericVersion `
        -p:AssemblyVersion=$numericVersion `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=none `
        --nologo -v minimal
}

Get-ChildItem -LiteralPath $stage -Filter '*.pdb' -Recurse | Remove-Item -Force
$toolsTarget = Join-Path $stage 'tools'
New-Item -ItemType Directory -Force -Path $toolsTarget | Out-Null
Copy-Item -Path (Join-Path $toolsCache '*') -Destination $toolsTarget -Recurse -Force

$packArguments = @(
    'vpk', 'pack',
    '--packId', $packId,
    '--packVersion', $resolvedVersion,
    '--packDir', $stage,
    '--mainExe', 'MiguelDownloader.exe',
    '--packTitle', 'Miguel Downloader',
    '--packAuthors', 'Gabriel Moreira',
    '--outputDir', $releases,
    '--channel', $channel,
    '--runtime', $Runtime,
    '--icon', $icon,
    '--aumid', 'MiguelDownloader.App',
    '--shortcuts', 'StartMenuRoot',
    '--instLicense', $licence,
    '--releaseNotes', $ReleaseNotes
)

if (-not $Portable) { $packArguments += @('--noPortable', 'true') }
if (-not $Installer) { $packArguments += @('--noInst', 'true') }

# Signing is optional locally and mandatory for a polished public channel once a certificate is
# available. Secrets stay in environment variables instead of appearing in process arguments in CI.
if ($env:VELOPACK_AZURE_SIGN_FILE) {
    $packArguments += @('--azureTrustedSignFile', $env:VELOPACK_AZURE_SIGN_FILE)
} elseif ($env:VELOPACK_SIGN_PARAMS) {
    $packArguments += @('--signParams', $env:VELOPACK_SIGN_PARAMS)
} else {
    Write-Warning 'Packages are not Authenticode-signed. Configure VELOPACK_AZURE_SIGN_FILE or VELOPACK_SIGN_PARAMS for public distribution.'
}

Write-Step 'Velopack installer, portable bundle and update feed'
Invoke-Native 'vpk pack' { dotnet @packArguments }

$currentAssets = Get-ChildItem -LiteralPath $releases -File | Where-Object {
    $_.Name -in @(
        "$packId-$channel-Setup.exe",
        "$packId-$channel-Portable.zip",
        'releases.win.json',
        'assets.win.json',
        'RELEASES'
    ) -or $_.Name -like "$packId-$resolvedVersion-*"
}

$requiredAssetNames = @(
    "$packId-$channel-Setup.exe",
    "$packId-$channel-Portable.zip",
    "$packId-$resolvedVersion-full.nupkg",
    'releases.win.json',
    'assets.win.json'
)
$missingAssets = $requiredAssetNames | Where-Object { $_ -notin $currentAssets.Name }
if ($missingAssets) {
    throw "Velopack did not produce required release assets: $($missingAssets -join ', ')"
}

Write-Step 'SHA-256 manifest'
$checksumPath = Join-Path $artifacts 'checksums.txt'
$checksumLines = foreach ($asset in ($currentAssets | Sort-Object Name)) {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "{0}  {1}" -f $hash, $asset.Name
}
$checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ascii
$checksumLines | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }

Remove-Item -LiteralPath $stage -Recurse -Force

Write-Host ''
Write-Host "Release assets in $releases" -ForegroundColor Green
$currentAssets | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-52} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
Write-Ok "checksums: $checksumPath"
