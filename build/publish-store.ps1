<#
.SYNOPSIS
    Builds the unsigned MSIX submission package that Microsoft signs after certification.

.DESCRIPTION
    Publishes the self-contained WPF application, adds the hash-pinned external tools and exact
    Partner Center identity, then packs and unpacks the result with the pinned official Windows
    SDK. The package intentionally has no developer certificate: its production signature is
    applied by Microsoft Store ingestion.
#>
[CmdletBinding()]
param(
    [string] $Version = '',
    [string] $Runtime = 'win-x64',
    [switch] $SkipTests,
    [switch] $SkipToolFetch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/MiguelDownloader.App/MiguelDownloader.App.csproj'
$packagingProject = Join-Path $PSScriptRoot 'store/MicrosoftStorePackage.csproj'
$manifestTemplate = Join-Path $PSScriptRoot 'store/AppxManifest.xml.template'
$storeAssets = Join-Path $PSScriptRoot 'store/Assets'
$artifacts = Join-Path $repoRoot 'artifacts'
$storeArtifacts = Join-Path $artifacts 'store'
$stage = Join-Path $storeArtifacts 'stage'
$buildOutput = Join-Path $storeArtifacts 'build-output'
$verification = Join-Path $storeArtifacts 'verified-package'
$codecSmoke = Join-Path $storeArtifacts 'codec-smoke'
$sdkPackages = Join-Path $storeArtifacts 'sdk-packages'
$toolsCache = Join-Path $PSScriptRoot 'tools-cache-store'
$storeToolLock = Join-Path $PSScriptRoot 'store/tools.lock.json'

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

function Clear-StoreDirectory {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }

    $resolved = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($storeArtifacts) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a path outside artifacts/store: $resolved"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$resolvedVersion = Get-ProjectVersion
if ($resolvedVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Microsoft Store packages require a stable three-part version; got '$resolvedVersion'."
}
if ($Runtime -ne 'win-x64') {
    throw "The reserved Store package currently supports win-x64 only; got '$Runtime'."
}

$packageVersion = "$resolvedVersion.0"
$packageName = "MiguelDownloader-$packageVersion-x64.msix"
$packagePath = Join-Path $storeArtifacts $packageName

Write-Host "Miguel Downloader Store package $packageVersion ($Runtime)" -ForegroundColor Green
New-Item -ItemType Directory -Force -Path $artifacts, $storeArtifacts | Out-Null
Clear-StoreDirectory -Path $stage
Clear-StoreDirectory -Path $buildOutput
Clear-StoreDirectory -Path $verification
Clear-StoreDirectory -Path $codecSmoke
if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }

if (-not $SkipTests) {
    Write-Step 'Automated tests'
    Invoke-Native 'dotnet test' {
        dotnet test (Join-Path $repoRoot 'tests/MiguelDownloader.Tests') `
            --configuration Release `
            --filter 'Category!=Integration&Category!=ToolInstall' `
            --nologo -v minimal
    }
}

Write-Step 'Official pinned MSIX tooling'
Invoke-Native 'Windows SDK BuildTools restore' {
    dotnet restore $packagingProject --packages $sdkPackages --nologo -v minimal
}

$makeAppxCandidates = @(Get-ChildItem -LiteralPath $sdkPackages -Filter 'makeappx.exe' -Recurse |
    Where-Object { $_.FullName -match '[\\/]x64[\\/]makeappx\.exe$' })
if ($makeAppxCandidates.Count -ne 1) {
    throw "Expected exactly one x64 MakeAppx executable; found $($makeAppxCandidates.Count)."
}
$makeAppx = $makeAppxCandidates[0].FullName
Write-Ok "MakeAppx: $makeAppx"

Write-Step 'Verified bundled tools'
& (Join-Path $PSScriptRoot 'fetch-tools.ps1') `
    -OutputDirectory $toolsCache `
    -ToolLockPath $storeToolLock `
    -Offline:$SkipToolFetch
if ($LASTEXITCODE -ne 0) { throw "Tool verification failed with exit code $LASTEXITCODE" }

foreach ($required in @('yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe', 'deno.exe', 'tools.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $toolsCache $required))) {
        throw "The verified tools cache is missing $required."
    }
}

# Standard Microsoft Store terms can conflict with GPLv3 distribution. The Store package uses
# BtbN's shared LGPL build instead, and this assertion fails closed if the pinned lock or cache is
# ever replaced with a GPL build by mistake.
$ffmpegPath = Join-Path $toolsCache 'ffmpeg.exe'
$ffmpegLicense = (& $ffmpegPath -L 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Could not inspect the Store FFmpeg licence." }
if ($ffmpegLicense -notmatch 'GNU Lesser General Public License' -or
    $ffmpegLicense -match '--enable-gpl(?:\s|$)') {
    throw 'The Microsoft Store package must contain the pinned LGPL FFmpeg build.'
}
Write-Ok 'FFmpeg licence profile: LGPL'

Write-Step 'Self-contained packaged application'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Invoke-Native 'dotnet publish' {
    dotnet publish $project `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        --output $stage `
        -p:BaseOutputPath=$buildOutput `
        -p:Version=$resolvedVersion `
        -p:FileVersion=$packageVersion `
        -p:AssemblyVersion=$packageVersion `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=none `
        --nologo -v minimal
}

Get-ChildItem -LiteralPath $stage -Filter '*.pdb' -Recurse | Remove-Item -Force
$toolsTarget = Join-Path $stage 'tools'
New-Item -ItemType Directory -Force -Path $toolsTarget | Out-Null
Copy-Item -Path (Join-Path $toolsCache '*') -Destination $toolsTarget -Recurse -Force

# Exercise the exact shared libraries copied into the package. This catches a missing DLL and also
# proves the software H.264 encoder used for explicit MP4 conversions is present before ingestion.
Write-Step 'Packaged FFmpeg smoke test'
New-Item -ItemType Directory -Force -Path $codecSmoke | Out-Null
$smokeVideo = Join-Path $codecSmoke 'openh264.mp4'
Invoke-Native 'FFmpeg LGPL encoding smoke test' {
    & (Join-Path $toolsTarget 'ffmpeg.exe') `
        -hide_banner -loglevel error `
        -f lavfi -i 'testsrc=size=320x180:rate=24' `
        -t 1 -pix_fmt yuv420p -c:v libopenh264 -y $smokeVideo
}
$probeJson = & (Join-Path $toolsTarget 'ffprobe.exe') `
    -v error -select_streams 'v:0' -show_entries 'stream=codec_name,width,height' -of json $smokeVideo
if ($LASTEXITCODE -ne 0) { throw 'FFprobe could not validate the Store encoder smoke test.' }
$probe = ($probeJson -join "`n") | ConvertFrom-Json
$stream = @($probe.streams) | Select-Object -First 1
if (-not $stream -or $stream.codec_name -ne 'h264' -or $stream.width -ne 320 -or $stream.height -ne 180) {
    throw 'The Store FFmpeg smoke test did not produce the expected H.264 stream.'
}
Clear-StoreDirectory -Path $codecSmoke
Write-Ok 'LGPL H.264 encode and probe succeeded'

$assetsTarget = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Force -Path $assetsTarget | Out-Null
Copy-Item -Path (Join-Path $storeAssets '*.png') -Destination $assetsTarget -Force

$manifest = (Get-Content -Raw $manifestTemplate).Replace('__PACKAGE_VERSION__', $packageVersion)
$manifestPath = Join-Path $stage 'AppxManifest.xml'
$manifest | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Step 'MSIX package'
Invoke-Native 'MakeAppx pack' { & $makeAppx pack /d $stage /p $packagePath /o /v }

# MakeAppx's own unpack pass validates the container and also proves that the manifest and every
# payload entry can be read back before the file is ever uploaded to Partner Center.
Write-Step 'Container verification'
New-Item -ItemType Directory -Force -Path $verification | Out-Null
Invoke-Native 'MakeAppx unpack' { & $makeAppx unpack /p $packagePath /d $verification /o /v }

$verifiedManifest = [xml](Get-Content -Raw (Join-Path $verification 'AppxManifest.xml'))
$identity = $verifiedManifest.Package.Identity
if ($identity.Name -ne 'ogabriels.MiguelDownloader' -or
    $identity.Publisher -ne 'CN=3A079706-B200-4828-AA8B-78BC8F2289C0' -or
    $identity.Version -ne $packageVersion) {
    throw 'The packed identity does not match the Partner Center reservation.'
}

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$packagePath.sha256"
"$hash  $packageName" | Set-Content -LiteralPath $checksumPath -Encoding ascii

Clear-StoreDirectory -Path $stage
Clear-StoreDirectory -Path $buildOutput
Clear-StoreDirectory -Path $verification

Write-Host ''
Write-Ok "package: $packagePath"
Write-Ok "SHA-256: $hash"
Write-Warning 'The submission package is intentionally unsigned. Microsoft signs it after Store certification.'
