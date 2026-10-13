<#
.SYNOPSIS
  Quick QNN smoke on a Snapdragon (Windows on ARM) host: venv + onnxruntime-qnn + a tiny model.

.DESCRIPTION
  Runs smoke_execution_providers.py against cpu and qnn. This exercises the ONNX Runtime QNN
  wheel directly, not Trackdub's Windows ML catalog path. See tools/provider-smoke/README.md for
  how to read the statuses (ok / fallback / fail / absent).

.PARAMETER Model
  ONNX model to run. When omitted, a tiny static-shape MatMul+Relu model is generated.

.PARAMETER Feed
  Optional .npz feed, forwarded to the harness (needed for models with dynamic dims).

.PARAMETER Python
  Python launcher to use. Must be an ARM64 build of Python on an ARM64 host.
#>
[CmdletBinding()]
param(
    [string]$Model,
    [string]$Feed,
    [string]$Python = 'python',
    [string]$Workdir = (Join-Path $PSScriptRoot '.qnn-smoke'),
    [string]$Results = (Join-Path $PSScriptRoot '.qnn-smoke\smoke-qnn.json')
)

$ErrorActionPreference = 'Stop'

$machine = (& $Python -c "import platform; print(platform.machine())").Trim()
Write-Host "Python machine: $machine"
if ($machine -ne 'ARM64') {
    throw "Python reports '$machine'; onnxruntime-qnn needs an ARM64 Python on the Snapdragon host."
}

New-Item -ItemType Directory -Force -Path $Workdir | Out-Null
$venv = Join-Path $Workdir 'venv'
if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) {
    & $Python -m venv $venv
}
$venvPython = Join-Path $venv 'Scripts\python.exe'
& $venvPython -m pip install --quiet --upgrade pip
& $venvPython -m pip install --quiet onnxruntime-qnn numpy onnx

if (-not $Model) {
    $Model = Join-Path $Workdir 'tiny-matmul.onnx'
    $generator = Join-Path $Workdir 'make_tiny_model.py'
    @'
import sys
import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

rng = np.random.default_rng(0)
weights = numpy_helper.from_array(rng.standard_normal((64, 64)).astype(np.float32), name="W")
graph = helper.make_graph(
    [
        helper.make_node("MatMul", ["X", "W"], ["Y"]),
        helper.make_node("Relu", ["Y"], ["Z"]),
    ],
    "tiny_matmul",
    [helper.make_tensor_value_info("X", TensorProto.FLOAT, [1, 64])],
    [helper.make_tensor_value_info("Z", TensorProto.FLOAT, [1, 64])],
    initializer=[weights],
)
model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
model.ir_version = 8
onnx.checker.check_model(model)
onnx.save(model, sys.argv[1])
'@ | Set-Content -Path $generator -Encoding utf8
    & $venvPython $generator $Model
    Write-Host "Generated $Model"
}

$harnessArgs = @(
    (Join-Path $PSScriptRoot 'smoke_execution_providers.py'),
    '--model', $Model,
    '--results', $Results,
    '--providers', 'cpu,qnn'
)
if ($Feed) { $harnessArgs += @('--feed', $Feed) }

& $venvPython @harnessArgs
Write-Host "Results written to $Results"
