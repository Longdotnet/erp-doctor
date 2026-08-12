param(
    [string]$Version = "latest",
    [string]$InstallDir = $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Programs\erp-doctor" } else { Join-Path $HOME ".erp-doctor\bin" }),
    [string]$ArchivePath,
    [string]$ChecksumsPath,
    [switch]$NoPathUpdate,
    [switch]$Help
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Show-Usage {
    @"
ERP Doctor installer for Windows x64

Usage:
  .\scripts\install.ps1
  .\scripts\install.ps1 -Version v0.15.0
  .\scripts\install.ps1 -InstallDir C:\Tools\erp-doctor

Offline/CI validation:
  .\scripts\install.ps1 -ArchivePath <zip> -ChecksumsPath <checksums.txt> -InstallDir <dir> -NoPathUpdate

Options:
  -Version <tag|latest>       GitHub Release tag (default: latest). A version like 0.15.0 is normalized to v0.15.0.
  -InstallDir <path>          Destination directory.
  -ArchivePath <path>         Use a local release ZIP instead of downloading.
  -ChecksumsPath <path>       Use a local checksums.txt instead of downloading.
  -NoPathUpdate               Do not modify the current user's PATH.
  -Help                       Show this help.
"@
}

if ($Help) {
    Show-Usage
    exit 0
}

if (-not $IsWindows) {
    throw "install.ps1 supports Windows only. Use scripts/install.sh on Linux."
}

if (-not [Environment]::Is64BitOperatingSystem) {
    throw "ERP Doctor self-contained releases currently support Windows x64 only."
}

$hasLocalArchive = -not [string]::IsNullOrWhiteSpace($ArchivePath)
$hasLocalChecksums = -not [string]::IsNullOrWhiteSpace($ChecksumsPath)
if ($hasLocalArchive -ne $hasLocalChecksums) {
    throw "ArchivePath and ChecksumsPath must be supplied together."
}

$assetName = "erp-doctor-win-x64.zip"
$repository = "Longdotnet/erp-doctor"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("erp-doctor-install-" + [Guid]::NewGuid().ToString("N"))
$downloadedArchive = Join-Path $tempRoot $assetName
$downloadedChecksums = Join-Path $tempRoot "checksums.txt"
$extractDir = Join-Path $tempRoot "extract"

function Resolve-ReleaseBaseUrl([string]$RequestedVersion) {
    if ([string]::Equals($RequestedVersion, "latest", [StringComparison]::OrdinalIgnoreCase)) {
        return "https://github.com/$repository/releases/latest/download"
    }

    $tag = $RequestedVersion.Trim()
    if (-not $tag.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $tag = "v$tag"
    }

    return "https://github.com/$repository/releases/download/$tag"
}

function Get-ExpectedSha256([string]$ChecksumFile, [string]$FileName) {
    foreach ($line in Get-Content -LiteralPath $ChecksumFile) {
        if ($line -match ('^([0-9a-fA-F]{64})\s+\*?' + [Regex]::Escape($FileName) + '$')) {
            return $Matches[1].ToLowerInvariant()
        }
    }

    throw "checksums.txt does not contain a SHA256 entry for $FileName."
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot, $extractDir | Out-Null

    if ($hasLocalArchive) {
        $resolvedArchive = (Resolve-Path -LiteralPath $ArchivePath).Path
        $resolvedChecksums = (Resolve-Path -LiteralPath $ChecksumsPath).Path
    }
    else {
        $baseUrl = Resolve-ReleaseBaseUrl $Version
        Write-Host "Downloading ERP Doctor Windows x64 release..."
        Invoke-WebRequest -Uri "$baseUrl/$assetName" -OutFile $downloadedArchive
        Invoke-WebRequest -Uri "$baseUrl/checksums.txt" -OutFile $downloadedChecksums
        $resolvedArchive = $downloadedArchive
        $resolvedChecksums = $downloadedChecksums
    }

    $expected = Get-ExpectedSha256 $resolvedChecksums $assetName
    $actual = (Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "SHA256 verification failed for $assetName. Expected $expected but got $actual."
    }

    Write-Host "SHA256 verified: $actual"
    Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $extractDir -Force

    $sourceExe = @(
        (Join-Path $extractDir "ErpDoctor.Cli.exe"),
        (Join-Path $extractDir "erp-doctor.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    if (-not $sourceExe) {
        throw "Release archive does not contain ErpDoctor.Cli.exe or erp-doctor.exe."
    }

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path (Join-Path $extractDir "*") -Destination $InstallDir -Recurse -Force

    $installedSourceExe = Join-Path $InstallDir (Split-Path -Leaf $sourceExe)
    $destinationExe = Join-Path $InstallDir "erp-doctor.exe"
    if (-not [string]::Equals($installedSourceExe, $destinationExe, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $installedSourceExe -Destination $destinationExe -Force
    }

    if (-not $NoPathUpdate) {
        $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
        $segments = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $alreadyPresent = $segments | Where-Object {
            [string]::Equals($_.TrimEnd('\'), $InstallDir.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
        }

        if (-not $alreadyPresent) {
            $newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) {
                $InstallDir
            }
            else {
                "$userPath;$InstallDir"
            }
            [Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")
        }

        if (-not (($env:Path -split ';') | Where-Object {
            [string]::Equals($_.TrimEnd('\'), $InstallDir.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
        })) {
            $env:Path = "$env:Path;$InstallDir"
        }
    }

    Write-Host "ERP Doctor installed: $destinationExe"
    if ($NoPathUpdate) {
        Write-Host "PATH was not modified."
    }
    else {
        Write-Host "Run: erp-doctor --help"
        Write-Host "A new terminal may be required to pick up the user PATH change."
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
