#Requires -Version 7.0
<#
.SYNOPSIS
    Validate TRT-RTX optimization of whisper-onnx models using Microsoft Olive.

.DESCRIPTION
    Runs the Olive TRT-RTX recipes for whisper-onnx encoder and decoder, then
    optionally measures encoder latency (rtfx) on Librispeech test.clean. Requires:
      - NVIDIA GPU with TRT-RTX (NvTensorRTRTXExecutionProvider) support
      - Model files already downloaded to the model cache (TRACKDUB_MODEL_CACHE, or
        %LOCALAPPDATA%\Trackdub\model-cache by default; run
        `dotnet run --project src/Trackdub.Tools -- ingest ...`)
      - olive-ai installed (run `.\tools\trackdub-optimize.ps1 -- --help` once to bootstrap venv)

    On success, records results to build/whisper-onnx-trtrtx-validation.json.
    Run .\tools\olive\Flip-WhisperOnnxTrtRtx.ps1 to apply manifest+test changes.

.PARAMETER ModelSize
    Which whisper-onnx model to validate. Accepted: tiny, base, small, medium, large-v3.

.PARAMETER SkipLatency
    Skip the encoder latency evaluation. Use when Librispeech dataset is unavailable.

.EXAMPLE
    .\tools\olive\Validate-WhisperOnnxTrtRtx.ps1 -ModelSize tiny
    .\tools\olive\Validate-WhisperOnnxTrtRtx.ps1 -ModelSize small -SkipLatency
#>

param(
    [ValidateSet('tiny', 'base', 'small', 'medium', 'large-v3')]
    [string] $ModelSize = 'tiny',
    [switch] $SkipLatency
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot   = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$VenvPath   = Join-Path $env:LOCALAPPDATA 'Trackdub\tools\olive-env-tensorrtrtx'
$OliveExe   = Join-Path $VenvPath 'Scripts\olive.exe'
$BuildDir   = Join-Path $RepoRoot 'build'
$ResultFile = Join-Path $BuildDir 'whisper-onnx-trtrtx-validation.json'

# A previous pass cannot authorize a later failed or incomplete run.
Remove-Item -LiteralPath $ResultFile -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# Model-size lookup (owner/repo match the model cache's download layout, i.e. the
# model id split on "/" into directory segments — not bundled-models.manifest.json's
# root_path)
# ---------------------------------------------------------------------------
$modelTable = @{
    'tiny'      = @{ Owner = 'onnx-community'; Repo = 'whisper-tiny';     RecipeDir = 'onnx-community-whisper-tiny'  }
    'base'      = @{ Owner = 'onnx-community'; Repo = 'whisper-base';     RecipeDir = 'onnx-community-whisper-base'  }
    'small'     = @{ Owner = 'onnx-community'; Repo = 'whisper-small';    RecipeDir = 'onnx-community-whisper-small' }
    'medium'    = @{ Owner = 'Xenova';         Repo = 'whisper-medium';   RecipeDir = 'Xenova-whisper-medium'         }
    'large-v3'  = @{ Owner = 'Xenova';         Repo = 'whisper-large-v3'; RecipeDir = 'Xenova-whisper-large-v3'       }
}

$entry          = $modelTable[$ModelSize]
. (Join-Path $PSScriptRoot 'TrtRtxOliveCommon.ps1')
$ModelCacheRoot = Resolve-TrackdubModelCacheRoot

# TensorRT RTX EP ABI plugin DLL, resolved like TensorRtRtxPluginLocator: TRACKDUB_TRT_RTX_EP_DIR,
# then the default install. Olive registers it via the recipe accelerator's (name, path) pair.
$TrtRtxEpDir    = if ($env:TRACKDUB_TRT_RTX_EP_DIR) { $env:TRACKDUB_TRT_RTX_EP_DIR } else { Join-Path $env:LOCALAPPDATA 'Trackdub\Providers\trt-rtx\0.5.0\cu13\win-x64' }
$TrtRtxEpPath   = Join-Path $TrtRtxEpDir 'onnxruntime_providers_nv_tensorrt_rtx.dll'
# The plugin's companion DLLs (tensorrt_rtx_1_7.dll, tensorrt_onnxparser_rtx_1_7.dll) live beside it but are
# resolved through the normal DLL search path, so the bundle dir must be on PATH for olive.
$env:PATH = "$TrtRtxEpDir;$env:PATH"
$modelRoot      = Join-Path $ModelCacheRoot (Join-Path $entry.Owner $entry.Repo)
$recipeDir      = Join-Path $RepoRoot 'resources\olive-recipes' $entry.RecipeDir 'NvTensorRtRtx'
$encoderSrc = Join-Path $modelRoot 'onnx\encoder_model.onnx'
$decoderSrc = Join-Path $modelRoot 'onnx\decoder_model.onnx'

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
Ensure-TrtRtxOliveEnvironment -VenvPath $VenvPath -BootstrapScript (Join-Path $PSScriptRoot 'Bootstrap-TrtRtxOliveVenv.ps1')

if (-not (Test-Path $encoderSrc)) {
    Write-Error "Encoder model not found: $encoderSrc`nDownload with: dotnet run --project src/Trackdub.Tools -- ingest --model $($entry.Owner)/$($entry.Repo)`nSearched model cache root: $ModelCacheRoot (override with `$env:TRACKDUB_MODEL_CACHE)"
    exit 1
}

if (-not (Test-Path $decoderSrc)) {
    Write-Error "Decoder model not found: $decoderSrc"
    exit 1
}

# ---------------------------------------------------------------------------
# Prune attention-probability outputs (Xenova exports carry output_attentions=True).
# When anything was pruned, recipes read from the pruned copy instead of the cache.
# ---------------------------------------------------------------------------
$PythonExe   = Join-Path $VenvPath 'Scripts\python.exe'
$pruneScript = Join-Path $PSScriptRoot 'prune_attention_outputs.py'
$prunedRoot  = Join-Path $BuildDir "whisper-$ModelSize-onnx-pruned-src"
$pruned      = @{}
foreach ($kind in @('encoder', 'decoder')) {
    $src = if ($kind -eq 'encoder') { $encoderSrc } else { $decoderSrc }
    $dst = Join-Path $prunedRoot "onnx\${kind}_model.onnx"
    Remove-Item -LiteralPath $dst, "$dst.data" -Force -ErrorAction SilentlyContinue
    & $PythonExe $pruneScript $src $dst
    if ($LASTEXITCODE -ne 0) { Write-Error "Output pruning failed for $kind (exit $LASTEXITCODE)."; exit 1 }
    $pruned[$kind] = Test-Path $dst
}
if ($pruned.Values -contains $true) {
    foreach ($kind in @('encoder', 'decoder')) {
        if (-not $pruned[$kind]) {
            $src = if ($kind -eq 'encoder') { $encoderSrc } else { $decoderSrc }
            Copy-Item $src (Join-Path $prunedRoot "onnx\${kind}_model.onnx") -Force
        }
    }
    $recipeModelRoot = $prunedRoot
} else {
    $recipeModelRoot = $modelRoot
}

# ---------------------------------------------------------------------------
# Patch recipes: substitute ${MODEL_ROOT} with the absolute model path
# ---------------------------------------------------------------------------
$TempDir = Join-Path $env:TEMP "trackdub-olive-trtrtx-$ModelSize-$([System.Diagnostics.Process]::GetCurrentProcess().Id)"
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null

function Resolve-Recipe {
    param([string] $SrcPath, [string] $DestPath)
    $content = Get-Content -Raw $SrcPath
    $content = $content -replace '\$\{MODEL_ROOT\}', ($recipeModelRoot -replace '\\', '/')
    $content = $content -replace '\$\{TRT_RTX_EP_PATH\}', ($TrtRtxEpPath -replace '\\', '/')
    Set-Content -Path $DestPath -Value $content -Encoding UTF8
}

$encoderRecipeDst = Join-Path $TempDir 'encoder_trtrtx_fp16.json'
$decoderRecipeDst = Join-Path $TempDir 'decoder_trtrtx_fp16.json'
$latencyRecipeDst = Join-Path $TempDir 'eval_latency.json'

Resolve-Recipe (Join-Path $recipeDir 'encoder_trtrtx_fp16.json') $encoderRecipeDst
Resolve-Recipe (Join-Path $recipeDir 'decoder_trtrtx_fp16.json') $decoderRecipeDst
Resolve-Recipe (Join-Path $recipeDir 'eval_latency.json') $latencyRecipeDst

$origDir = Get-Location
Set-Location $RepoRoot

$results = [ordered]@{
    model_size    = $ModelSize
    model_root    = $modelRoot
    timestamp_utc = $null   # filled at end
    encoder       = $null
    decoder       = $null
    latency       = $null
    pass          = $false
}

try {
    # ---------------------------------------------------------------------------
    # Step 1: Optimize encoder
    # ---------------------------------------------------------------------------
    Write-Host ""
    Write-Host "=== Encoder optimization (fp16 + TRT-RTX session params) ===" -ForegroundColor Cyan
    & $OliveExe run --config $encoderRecipeDst
    if ($LASTEXITCODE -ne 0) { Write-Error "Encoder optimization failed (exit $LASTEXITCODE)."; exit 1 }
    $results.encoder = @{ status = 'ok'; output = "build/whisper-$ModelSize-onnx_encoder_trtrtx_fp16" }

    # ---------------------------------------------------------------------------
    # Step 2: Optimize decoder
    # ---------------------------------------------------------------------------
    Write-Host ""
    Write-Host "=== Decoder optimization (fp16 + TRT-RTX session params) ===" -ForegroundColor Cyan
    & $OliveExe run --config $decoderRecipeDst
    if ($LASTEXITCODE -ne 0) { Write-Error "Decoder optimization failed (exit $LASTEXITCODE)."; exit 1 }
    $results.decoder = @{ status = 'ok'; output = "build/whisper-$ModelSize-onnx_decoder_trtrtx_fp16" }

    # ---------------------------------------------------------------------------
    # Step 3 (optional): encoder latency evaluation (rtfx)
    # ---------------------------------------------------------------------------
    if (-not $SkipLatency) {
        Write-Host ""
        Write-Host "=== Encoder latency evaluation on Librispeech test.clean (64 samples) ===" -ForegroundColor Cyan
        & $OliveExe run --config $latencyRecipeDst
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Latency evaluation failed (exit $LASTEXITCODE). Check network access to HuggingFace datasets."
            $results.latency = @{ status = 'failed' }
        } else {
            $results.latency = @{ status = 'ok' }
        }
    } else {
        Write-Host "Skipping encoder latency evaluation (-SkipLatency)." -ForegroundColor Yellow
        $results.latency = @{ status = 'skipped' }
    }

    # ---------------------------------------------------------------------------
    # Step 4: Stage combined output for the C# validation test
    # ---------------------------------------------------------------------------
    Write-Host ""
    Write-Host "=== Staging combined output for C# validation test ===" -ForegroundColor Cyan

    $StagingDir     = Join-Path $BuildDir "whisper-$ModelSize-onnx-trtrtx-validated"
    $StagingOnnxDir = Join-Path $StagingDir "onnx"
    New-Item -ItemType Directory -Force -Path $StagingOnnxDir | Out-Null

    # Carry over tokenizer/config files from original model root.
    foreach ($asset in @("vocab.json", "config.json", "tokenizer.json", "tokenizer_config.json",
                          "special_tokens_map.json", "preprocessor_config.json")) {
        $assetSrc = Join-Path $modelRoot $asset
        if (Test-Path $assetSrc) {
            Copy-Item $assetSrc (Join-Path $StagingDir $asset) -Force
        }
    }

    # Copy optimized encoder (first *.onnx found in Olive output dir).
    $encoderOutputDir = Join-Path $BuildDir "whisper-$ModelSize-onnx_encoder_trtrtx_fp16"
    $encoderOnnxSrc   = Get-ChildItem $encoderOutputDir -Filter "*.onnx" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($encoderOnnxSrc) {
        Copy-Item $encoderOnnxSrc.FullName (Join-Path $StagingOnnxDir "encoder_model.onnx") -Force
        Get-ChildItem $encoderOnnxSrc.Directory -Filter "*.onnx.data" -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $StagingOnnxDir "encoder_model.onnx.data") -Force }
    } else {
        Write-Warning "No encoder *.onnx found in $encoderOutputDir — staging incomplete."
    }

    # Copy optimized decoder.
    $decoderOutputDir = Join-Path $BuildDir "whisper-$ModelSize-onnx_decoder_trtrtx_fp16"
    $decoderOnnxSrc   = Get-ChildItem $decoderOutputDir -Filter "*.onnx" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($decoderOnnxSrc) {
        Copy-Item $decoderOnnxSrc.FullName (Join-Path $StagingOnnxDir "decoder_model.onnx") -Force
        Get-ChildItem $decoderOnnxSrc.Directory -Filter "*.onnx.data" -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $StagingOnnxDir "decoder_model.onnx.data") -Force }
    } else {
        Write-Warning "No decoder *.onnx found in $decoderOutputDir — staging incomplete."
    }

    $results.staging_dir = $StagingDir
    $results.pass = ($null -ne $encoderOnnxSrc) -and ($null -ne $decoderOnnxSrc)

} finally {
    Set-Location $origDir
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Write result file
# ---------------------------------------------------------------------------
$results.timestamp_utc = (Get-Date).ToUniversalTime().ToString('o')
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
$results | ConvertTo-Json -Depth 4 | Set-Content -Path $ResultFile -Encoding UTF8

Write-Host ""
if ($results.pass) {
    Write-Host "PASS — TRT-RTX optimization succeeded for whisper-$ModelSize." -ForegroundColor Green
    Write-Host "Results written to: $ResultFile"
    Write-Host ""
    Write-Host "Staging directory: $($results.staging_dir)"
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. dotnet test tests/Trackdub.Inference.Tests --filter 'FullyQualifiedName~WhisperOnnxTrtRtx'"
    Write-Host "     (WhisperOnnxTrtRtx_${ModelSize}Model_SessionLoadsAndTranscribesSilence now runs on this machine;"
    Write-Host "     it skips automatically wherever build/whisper-${ModelSize}-onnx-trtrtx-validated is absent, e.g. CI.)"
    Write-Host "  2. If that passes: run .\tools\olive\Flip-WhisperOnnxTrtRtx.ps1 to enable trt-rtx in the manifest and tests."
} else {
    Write-Host "FAIL — see errors above." -ForegroundColor Red
    exit 1
}
