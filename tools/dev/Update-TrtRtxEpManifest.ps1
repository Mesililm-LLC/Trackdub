#Requires -Version 7.0
<#
.SYNOPSIS
  Refreshes runtime/trt-rtx-ep.manifest.json for a new TensorRT RTX EP ABI release.

.DESCRIPTION
  Downloads win-x64 and linux-x64 archives from the NVIDIA GitHub release tag(s), computes
  SHA-256 and size, and patches runtime/trt-rtx-ep.manifest.json.

  NVIDIA does not publish every platform on every tag (v0.4.2 was Windows-only); pass
  -LinuxVersion to pin the linux-x64 package to an older tag. It is recorded as a per-package
  "version" and drives that platform's install directory. -TrtRtxRuntimeVersion is the
  TensorRT-RTX runtime the bundle vendors (e.g. tensorrt_rtx_1_7.dll -> 1.7.x); it must match
  TensorRtRtxProviderConstants.BundledTrtRtxRuntimeVersion because it feeds the smoke-verdict
  and EP-context invalidation fingerprints.

  After running, update TensorRtRtxProviderConstants, user-facing install hints, run smoke
  tests, and commit the manifest. See docs/internal/tensorrt-rtx-ep-abi-plugin.md.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$TrtRtxRuntimeVersion,

    [string]$LinuxVersion,

    [string]$CudaVariant = 'cu13',

    [string]$ManifestPath,

    [string]$LicenseUrl = 'https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html',

    [switch]$WhatIf
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

function Get-ReleaseArchiveSpec([string]$ReleaseVersion, [string]$Cuda, [string]$Rid) {
    $tag = "v$ReleaseVersion"
    $base = "https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/releases/download/$tag"
    if ($Rid -eq 'win-x64') {
        return @{
            ArchiveUrl = "$base/TensorRT-RTX-EP-ABI-v$ReleaseVersion-$Cuda.zip"
            ArchiveKind = 'zip'
        }
    }

    if ($Rid -eq 'linux-x64') {
        return @{
            ArchiveUrl = "$base/TensorRT-RTX-EP-ABI-v$ReleaseVersion-$Cuda-linux-x86_64.zip"
            ArchiveKind = 'zip'
        }
    }

    throw "Unsupported RID '$Rid'."
}

function Measure-RemoteArchive([string]$Url) {
    $tempFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "trt-rtx-manifest-$([Guid]::NewGuid().ToString('N'))")
    try {
        Write-Host "Downloading $Url ..."
        Invoke-WebRequest -Uri $Url -OutFile $tempFile -UseBasicParsing
        $info = Get-Item $tempFile
        $hash = (Get-FileHash -Path $tempFile -Algorithm SHA256).Hash.ToLowerInvariant()
        return @{
            SizeBytes = $info.Length
            Sha256 = $hash
        }
    }
    finally {
        if (Test-Path $tempFile) {
            Remove-Item -Path $tempFile -Force -ErrorAction SilentlyContinue
        }
    }
}

$repoRoot = Get-RepoRoot
if (-not $ManifestPath) {
    $ManifestPath = Join-Path $repoRoot 'runtime/trt-rtx-ep.manifest.json'
}

if (-not $LinuxVersion) {
    $LinuxVersion = $Version
}

$packages = [ordered]@{}
foreach ($rid in @('win-x64', 'linux-x64')) {
    $ridVersion = if ($rid -eq 'linux-x64') { $LinuxVersion } else { $Version }
    $spec = Get-ReleaseArchiveSpec -ReleaseVersion $ridVersion -Cuda $CudaVariant -Rid $rid
    $metrics = Measure-RemoteArchive -Url $spec.ArchiveUrl
    $packages[$rid] = [ordered]@{}
    if ($ridVersion -ne $Version) {
        $packages[$rid].version = $ridVersion
    }

    $packages[$rid] += [ordered]@{
        archiveUrl = $spec.ArchiveUrl
        archiveKind = $spec.ArchiveKind
        sha256 = $metrics.Sha256
        sizeBytes = $metrics.SizeBytes
    }
    Write-Host "$rid : sha256=$($metrics.Sha256) size=$($metrics.SizeBytes)"
}

$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    cudaVariant = $CudaVariant
    trtRtxRuntimeVersion = $TrtRtxRuntimeVersion
    licenseUrl = $LicenseUrl
    packages = $packages
}

$json = ($manifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine

if ($WhatIf) {
    Write-Host $json
    exit 0
}

Set-Content -Path $ManifestPath -Value $json -Encoding utf8NoBOM -NoNewline
Write-Host "Updated $ManifestPath"
Write-Host ''
Write-Host 'Checklist:'
Write-Host "  1. Update TensorRtRtxProviderConstants (BundledVersionWindows/Linux, BundledCudaVariant, BundledTrtRtxRuntimeVersion, runtime DLL names)."
Write-Host '  2. Run tools/dev/Fetch-TrtRtxEp.ps1 and trackdub providers trt-rtx status.'
Write-Host '  3. Run TRT smoke / inference tests on Windows and Linux.'
Write-Host '  4. Commit runtime/trt-rtx-ep.manifest.json and constant updates.'
