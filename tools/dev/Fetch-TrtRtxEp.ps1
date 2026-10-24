#Requires -Version 7.0
<#
.SYNOPSIS
  Downloads and installs the pinned TensorRT RTX EP ABI plugin bundle for the current OS.

.DESCRIPTION
  Reads runtime/trt-rtx-ep.manifest.json, downloads the win-x64 or linux-x64 archive,
  verifies SHA-256 and size, extracts native libraries into a flat provider directory under
  the Trackdub user data root, and prints TRACKDUB_TRT_RTX_EP_DIR for the current session.

  Used by local dev and CI (.github/workflows/trt-rtx-smoke.yml). Does not accept the
  NVIDIA license; for product installs use Model Manager or `trackdub providers trt-rtx install`.
#>
[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$InstallRoot,
    [string]$RuntimeIdentifier,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    $dir = $PSScriptRoot
    while ($dir) {
        if (Test-Path (Join-Path $dir 'Trackdub.slnx')) {
            return (Resolve-Path $dir).Path
        }

        $parent = Split-Path $dir -Parent
        if (-not $parent -or $parent -eq $dir) {
            throw 'Could not locate Trackdub.sln from tools/dev.'
        }

        $dir = $parent
    }
}

function Get-DefaultRuntimeIdentifier {
    if ($IsWindows) { return 'win-x64' }
    if ($IsLinux) { return 'linux-x64' }
    throw 'TensorRT RTX EP ABI is supported on Windows and Linux x64 only.'
}

function Get-RequiredFileNames([string]$Rid) {
    if ($Rid -eq 'win-x64') {
        return @(
            'onnxruntime_providers_nv_tensorrt_rtx.dll',
            'tensorrt_rtx_1_7.dll',
            'tensorrt_onnxparser_rtx_1_7.dll'
        )
    }

    if ($Rid -eq 'linux-x64') {
        return @(
            'libonnxruntime_providers_nv_tensorrt_rtx.so',
            'libtensorrt_rtx.so',
            'libtensorrt_onnxparser_rtx.so'
        )
    }

    throw "Unsupported runtime identifier '$Rid'."
}

function Test-BundleReady([string]$Directory, [string[]]$RequiredFiles) {
    if (-not (Test-Path $Directory)) { return $false }
    foreach ($name in $RequiredFiles) {
        if (-not (Test-Path (Join-Path $Directory $name))) { return $false }
    }

    return $true
}

function Get-ArchiveExtension([string]$ArchiveKind) {
    switch ($ArchiveKind.ToLowerInvariant()) {
        'zip' { return '.zip' }
        'tar.gz' { return '.tar.gz' }
        default { throw "Unsupported archive kind '$ArchiveKind'." }
    }
}

function Expand-TrtArchive([string]$ArchivePath, [string]$ArchiveKind, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    switch ($ArchiveKind.ToLowerInvariant()) {
        'zip' {
            if ($IsWindows) {
                Expand-Archive -Path $ArchivePath -DestinationPath $Destination -Force
            } else {
                # Expand-Archive drops Unix symlinks (the linux asset's SONAME chain); unzip keeps them.
                unzip -q -o $ArchivePath -d $Destination
                if ($LASTEXITCODE -ne 0) { throw "unzip failed with exit code $LASTEXITCODE." }
            }
        }
        'tar.gz' {
            tar -xzf $ArchivePath -C $Destination
        }
        default { throw "Unsupported archive kind '$ArchiveKind'." }
    }
}

function Copy-NativeLibrariesFlat([string]$SourceRoot, [string]$DestinationRoot) {
    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
    $patterns = if ($IsWindows) { @('*.dll') } else { @('*.so', '*.so.*') }
    foreach ($pattern in $patterns) {
        Get-ChildItem -Path $SourceRoot -Filter $pattern -Recurse -File |
            Where-Object { $_.Extension -ine '.pdb' } |
            ForEach-Object {
                if ($IsWindows) {
                    Copy-Item -Path $_.FullName -Destination (Join-Path $DestinationRoot $_.Name) -Force
                } else {
                    # -P keeps SONAME symlinks as links instead of duplicating the 200+ MB runtime.
                    cp -P -f $_.FullName (Join-Path $DestinationRoot $_.Name)
                }
            }
    }
}

function Assert-RequiredFiles([string]$Directory, [string[]]$RequiredFiles) {
    $missing = @($RequiredFiles | Where-Object { -not (Test-Path (Join-Path $Directory $_)) })
    if ($missing.Count -gt 0) {
        throw "Bundle install is missing required files: $($missing -join ', ')"
    }
}

$repoRoot = Get-RepoRoot
if (-not $ManifestPath) {
    $ManifestPath = Join-Path $repoRoot 'runtime/trt-rtx-ep.manifest.json'
}

$manifest = Get-Content -Raw -Path $ManifestPath | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported manifest schemaVersion $($manifest.schemaVersion) (expected 1)."
}

$rid = if ($RuntimeIdentifier) { $RuntimeIdentifier } else { Get-DefaultRuntimeIdentifier }
if (-not $manifest.packages.PSObject.Properties.Name.Contains($rid)) {
    throw "Manifest does not contain package entry for '$rid'."
}

$package = $manifest.packages.$rid
$requiredFiles = Get-RequiredFileNames $rid

$userDataRoot = if ($InstallRoot) {
    $InstallRoot
} elseif ($env:TRACKDUB_DATA_ROOT) {
    $env:TRACKDUB_DATA_ROOT
} elseif ($IsWindows) {
    Join-Path $env:LOCALAPPDATA 'Trackdub'
} else {
    Join-Path $env:HOME '.local/share/Trackdub'
}

# Packages may pin their own EP ABI release when NVIDIA does not publish every platform on every tag.
$packageVersion = if ($package.PSObject.Properties.Name -contains 'version' -and $package.version) { $package.version } else { $manifest.version }
$installDirectory = Join-Path $userDataRoot "Providers/trt-rtx/$packageVersion/$($manifest.cudaVariant)/$rid"

if (-not $Force -and (Test-BundleReady $installDirectory $requiredFiles)) {
    Write-Host "TensorRT RTX EP bundle already installed at '$installDirectory'."
    $env:TRACKDUB_TRT_RTX_EP_DIR = $installDirectory
    Write-Host "TRACKDUB_TRT_RTX_EP_DIR=$installDirectory"
    exit 0
}

Write-Host "Downloading TensorRT RTX EP ABI v$packageVersion $($manifest.cudaVariant) ($rid)..."
$parentDirectory = Split-Path $installDirectory -Parent
New-Item -ItemType Directory -Path $parentDirectory -Force | Out-Null

$archiveExtension = Get-ArchiveExtension $package.archiveKind
$tempArchive = Join-Path $parentDirectory "trt-rtx-ep-download-$([Guid]::NewGuid().ToString('N'))$archiveExtension"
$tempExtract = Join-Path $parentDirectory "trt-rtx-ep-extract-$([Guid]::NewGuid().ToString('N'))"
$tempInstall = Join-Path $parentDirectory "trt-rtx-ep-staging-$([Guid]::NewGuid().ToString('N'))"

try {
    Invoke-WebRequest -Uri $package.archiveUrl -OutFile $tempArchive -UseBasicParsing

    $length = (Get-Item $tempArchive).Length
    if ($package.sizeBytes -gt 0 -and $length -ne $package.sizeBytes) {
        throw "Archive size mismatch. Expected $($package.sizeBytes), got $length."
    }

    if ($package.sha256) {
        $hash = (Get-FileHash -Path $tempArchive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $package.sha256.ToLowerInvariant()) {
            throw 'Archive checksum verification failed.'
        }
    }

    Expand-TrtArchive -ArchivePath $tempArchive -ArchiveKind $package.archiveKind -Destination $tempExtract
    Copy-NativeLibrariesFlat -SourceRoot $tempExtract -DestinationRoot $tempInstall
    Assert-RequiredFiles -Directory $tempInstall -RequiredFiles $requiredFiles

    if (Test-Path $installDirectory) {
        Remove-Item -Path $installDirectory -Recurse -Force
    }

    Move-Item -Path $tempInstall -Destination $installDirectory
    Write-Host "TensorRT RTX EP bundle installed to '$installDirectory'."
    $env:TRACKDUB_TRT_RTX_EP_DIR = $installDirectory
    Write-Host "TRACKDUB_TRT_RTX_EP_DIR=$installDirectory"
}
finally {
    if (Test-Path $tempExtract) { Remove-Item -Path $tempExtract -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $tempInstall) { Remove-Item -Path $tempInstall -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $tempArchive) { Remove-Item -Path $tempArchive -Force -ErrorAction SilentlyContinue }
}
