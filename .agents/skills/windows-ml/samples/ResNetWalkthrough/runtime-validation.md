# ResNet runtime validation

Executed 2026-10-10. These are observations of this standalone sample on one machine, not Microsoft platform guarantees or Trackdub pipeline readiness claims. Platform guidance and model acquisition remain sourced to the [Microsoft Learn walkthrough](https://learn.microsoft.com/windows/ai/models/get-started-onnx-winui).

## Setup

- Windows 11 Pro, build `26200`.
- NVIDIA GeForce RTX 5070, driver `32.0.16.1742`.
- .NET SDK `10.0.401`; sample targets .NET 8 Windows x64, with runtime `8.0.31` installed.
- Windows ML `2.4.89`; System.Drawing.Common `8.0.31`.
- Model: the raw `resnet50-v2-7.onnx` artifact linked by Learn, downloaded into a temporary directory outside the repository. Size: `102442452` bytes. SHA-256: `79102261EB6E5FD7AF5D27F41316293E388C5CB691E5D25BFB035C4F64FEFE31`.
- Input: a locally generated 256×256 RGB gradient (`R=x`, `G=y`, `B=128`), saved as PNG. This exercises decoding, preprocessing and inference; it is not a labeled classification-accuracy test.

The artifact reported input `data`, float32 `[-1,3,224,224]`, and output `resnetv24_dense0_fwd`, float32 `[-1,1000]`. The original sample incorrectly rejected the dynamic batch. The corrected guard accepts fixed batch `1` or dynamic batch `-1`, always binds batch `1`, and still rejects other batch sizes, ranks and non-batch shape changes. Seven focused shape-guard checks passed.

## Executed results

| Path | Result | What this establishes |
| --- | --- | --- |
| CPU, explicit `CPUExecutionProvider`, CPU device 0 | Exit 0; inference returned 1000 finite scores and top-five output | Model loading, metadata, image preprocessing, binding, CPU-configured session and result handling succeeded |
| DirectML, explicit `DmlExecutionProvider`, GPU device 0 | Exit 0; top-five output matched CPU to the displayed four decimal percentage places | DirectML-configured session creation and inference succeeded; no per-node placement trace was captured |
| DirectML with `--cache` | Exit 0; logged `Using original model; no unvalidated compiled artifact was selected.` | The no-valid-compiled-output fallback ran successfully; compiled-artifact promotion and reuse were not exercised |
| TensorRT RTX without `--download` | Exit 3; catalog reported `NotPresent` | The sample stopped without silently downloading or substituting a provider |
| TensorRT RTX, catalog preparation and original-model inference | Exit 0 after acquisition completed; dynamic-shape profile warnings appeared | Preparation, registration and inference on an explicitly configured TensorRT RTX session succeeded |
| TensorRT RTX, corrected canonical-name cache lookup | Exit 0; compilation reported `EP_SUPPORTED_OPTIMAL` and published the cache index | A compiled artifact was validated against the selected device and loaded successfully |
| TensorRT RTX, subsequent process with `--cache` | Exit 0; logged `Reusing source-matched, EP-validated compiled model.` | Cross-process cache validation and reuse succeeded; top-five output matched the preceding compiled run |

CPU and DirectML both printed:

```text
Class index 644: 28.9243%
Class index 604: 11.6401%
Class index 499: 5.8384%
Class index 714: 4.7572%
Class index 111: 2.7703%
```

TensorRT RTX preparation with `--download --cache` took approximately four minutes before device enumeration proceeded. During acquisition, a fresh no-download process still reported `NotPresent`, even though `Get-AppxPackage` already listed the EP package. After preparation completed, both `Microsoft.WinML.NVIDIA.TRT-RTX.EP.2` and `Microsoft.WinML.NVIDIA.TRT-RTX.EP.Framework.2` version `2.30.49.0` were listed. The intermediate package observation did not establish catalog readiness.

The device reported `NvTensorRTRTXExecutionProvider`, while the requested name was `NvTensorRtRtxExecutionProvider`. Case-insensitive selection worked, but using the requested spelling for compatibility metadata returned no usable metadata. The corrected cache path uses the device's actual `EpName`; compilation then reported `EP_SUPPORTED_OPTIMAL`. This is a measured sample fix, not a claim about every provider's name normalization.

The validated compiled TensorRT RTX run and the subsequent cache reuse both printed:

```text
Class index 644: 29.2965%
Class index 604: 11.6305%
Class index 499: 5.7555%
Class index 714: 4.6999%
Class index 111: 2.7831%
```

The uncached TensorRT RTX run printed `29.0548%` for the highest class. Top-five class indices stayed the same across the tested paths, but scores differed from CPU/DirectML and between TensorRT engine builds. No numerical tolerance or accuracy acceptance criterion was established. Native diagnostics warned that no explicit optimization profile was specified and that minimum-profile reshape dimensions were inconsistent. These warnings were retained in the raw logs; no undocumented provider options were added to suppress them.

## Limits

No semantic labels or labeled photograph were evaluated. Matching displayed scores do not establish general numerical equivalence. No ETW/operator placement, GPU utilization or latency benchmark was captured. OpenVINO, QNN, VitisAI, MIGraphX and experimental WebGPU were not run. Driver/runtime update invalidation and missing/corrupted sidecars were not tested. This evidence does not qualify any Trackdub stage or a model/provider combination beyond the paths above.

The original model and raw console logs remain in the local temporary run directory; model/image binaries are not committed. See the adjacent README for reproducible commands and [Microsoft's logging procedure](https://learn.microsoft.com/windows/ai/new-windows-ml/logs) for stronger placement evidence.

## PR verification, 2026-10-15

Rebuilt the isolated sample from current core main with Release warnings as errors: zero warnings and zero errors. Invoked the compiled shape guard against eight cases: fixed and dynamic input batch accepted; batch 2, a dynamic spatial dimension, and wrong rank rejected; fixed and dynamic output batch accepted; batch 0 rejected. The no-argument usage path returned its documented exit code 2. This publication pass did not repeat model/provider inference, downloads or compilation-cache execution; the October 10 runtime observations above remain historical evidence.
