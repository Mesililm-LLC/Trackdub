# Executable C# model walkthrough

Microsoft Learn is the sole source for the platform and model guidance below. Checked 2026-10-10. The console interface, validation guards and cache layout are sample implementation choices, not Microsoft requirements or Trackdub policy.

## Prerequisites and packages

Use Windows x64 and the .NET 8 SDK or later, with a .NET 8 runtime to execute this sample. The project targets `net8.0-windows10.0.18362.0` and `win-x64`. CPU and included DirectML have broader OS support than catalog EPs; acquiring catalog EPs requires Windows 11 24H2/build 26100 or later. For ARM64, change both `RuntimeIdentifier` and `PlatformTarget` to ARM64 and validate on that hardware. [Windows ML prerequisites](https://learn.microsoft.com/windows/ai/new-windows-ml/get-started), [deployment](https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app).

The standalone project references:

| Package | Sample version | Purpose |
| --- | --- | --- |
| `Microsoft.Windows.AI.MachineLearning` | `2.4.89` | Self-contained Windows ML and its included ONNX Runtime APIs |
| `System.Drawing.Common` | `8.0.31` | Windows image decoding, cropping and resizing |

These versions are the compiled sample snapshot, not Trackdub pins or a promise about future package support. Recheck the [Windows ML/ORT mapping](https://learn.microsoft.com/windows/ai/new-windows-ml/onnx-versions). The project deliberately isolates its restore/build settings from the enclosing repository. It does not add a stock native ORT package or any production project dependency. [Windows ML package setup](https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app), [System.Drawing API](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.drawimage).

## Acquire the exact model and an image

1. Open Microsoft's [ONNX WinUI walkthrough, Add the model](https://learn.microsoft.com/windows/ai/models/get-started-onnx-winui#add-the-model-to-your-project).
2. Follow its model artifact link and download the raw `resnet50-v2-7.onnx` file. The model file is an input artifact; no technical guidance is taken from that external site. Do not save the HTML viewer or a Git LFS pointer as `.onnx`.
3. Place it outside the source tree, for example `C:\WinMlSample\resnet50-v2-7.onnx`. Supply a local JPEG or PNG, for example `C:\WinMlSample\photo.jpg`.

The sample does not bundle, auto-download or assert redistribution rights for a model. It is specifically for this single-file ResNet export, not arbitrary ONNX or GenAI exports. A different export may need different tensor types, shapes, preprocessing and postprocessing. [Model sourcing](https://learn.microsoft.com/windows/ai/new-windows-ml/models), [model distribution](https://learn.microsoft.com/windows/ai/new-windows-ml/model-distribution).

## Build and run the CPU baseline

From this directory:

```powershell
dotnet build .\ResNetWalkthrough.csproj -c Release -m:1
dotnet run --project .\ResNetWalkthrough.csproj -c Release --no-build -- C:\WinMlSample\resnet50-v2-7.onnx C:\WinMlSample\photo.jpg
```

No catalog acquisition occurs in this default CPU path. The program:

1. Enumerates runtime devices and explicitly appends the requested CPU device.
2. Creates the session and prints input/output names, element types and dimensions.
3. Requires exactly one float32 input `[batch,3,224,224]` and one float32 output `[batch,1000]`. The batch dimension may be fixed to `1` or dynamic (`-1`), as in the linked model; the sample always binds one image. Other dynamic dimensions, unexpected shapes, FP16 or quantized input exports require a different adapter.
4. Decodes RGB pixels, center-crops/resizes to 224×224, and creates an NCHW `DenseTensor<float>`. Each channel uses `(value / 255 - mean) / standardDeviation`, with means `[0.485,0.456,0.406]` and deviations `[0.229,0.224,0.225]` from Learn. Drawing-based resampling is this sample's adaptation; it is not asserted to be bit-identical to Learn's ImageSharp implementation.
5. Binds the actual metadata input name with `OrtValue`, runs once, and disposes inputs, outputs, session and options.
6. Requires 1000 finite logits, applies numerically stable softmax, and prints the five highest class indices and scores. Scores are model outputs, not calibrated accuracy evidence. Semantic labels are intentionally omitted; Learn delegates the full ordered label map to an external sample. Do not invent or alphabetize it.

Sources: [Learn preprocessing, tensor binding and inference](https://learn.microsoft.com/windows/ai/models/get-started-onnx-winui#load-and-analyze-an-image), [metadata and stable softmax patterns](https://learn.microsoft.com/windows/ai/new-windows-ml/tutorial), [Bitmap.GetPixel](https://learn.microsoft.com/dotnet/api/system.drawing.bitmap.getpixel), [Graphics.DrawImage](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.drawimage).

## Select and prepare an accelerator explicitly

Arguments after the image are `EpName`, hardware type (`CPU`, `GPU`, `NPU`), and device index within that EP/type group. The sample logs matching devices and selects exactly one; an unavailable selection fails rather than silently substituting another provider.

```powershell
# Included DirectML; no catalog download.
dotnet run --project .\ResNetWalkthrough.csproj -c Release --no-build -- C:\WinMlSample\resnet50-v2-7.onnx C:\WinMlSample\photo.jpg DmlExecutionProvider GPU 0

# Catalog example: permit download explicitly, then validate compiled artifacts.
dotnet run --project .\ResNetWalkthrough.csproj -c Release --no-build -- C:\WinMlSample\resnet50-v2-7.onnx C:\WinMlSample\photo.jpg OpenVINOExecutionProvider NPU 0 --download --cache
```

Choose other names and hardware classes from [provider configuration coverage](../../canon/provider-configuration-coverage.md). This is not a claim that this particular export works on every EP. The sample passes an empty provider-options dictionary; it does not turn the documentation's `provider_specific_option` placeholder into a real setting. [Explicit selection](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers).

For catalog EPs, the sample finds only the requested provider. `NotPresent` without `--download` stops. Otherwise it awaits `EnsureReadyAsync()`, checks `Status`, distinguishes `InProgress` from failure, logs HRESULT/diagnostic text, and calls `TryRegister()` only after successful preparation. `NotReady` can require adding an already-installed package to the app dependency graph even when downloads are disabled. [Install states](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers), [registration](https://learn.microsoft.com/windows/ai/new-windows-ml/register-execution-providers).

WebGPU additionally requires an experimental Windows ML package; the stable sample pin does not establish availability. Change and rebuild the package only for a deliberate experimental run. The sample explicitly prepares/registers WebGPU and unregisters it after session disposal. Unsupported graph partitions can still run on CPU. [WebGPU lifecycle and restrictions](https://learn.microsoft.com/windows/ai/new-windows-ml/webgpu-ep).

## Validate compiled artifacts

`--cache` applies to GPU/NPU runs. The sample chooses an **optimal-only** cache policy: `EP_SUPPORTED_PREFER_RECOMPILATION` is supported, but this policy recompiles it. `EP_UNSUPPORTED`, `EP_NOT_APPLICABLE` or missing metadata do not authorize reuse. A compatibility-evaluation error selects the original model for the current run and does not publish a new artifact. [Compatibility meanings and error policy](https://learn.microsoft.com/windows/ai/new-windows-ml/model-compilation).

The cache lives in `%LOCALAPPDATA%\WindowsMlSkill\ResNetWalkthrough`. Its identity includes the source SHA-256, EP name, device index, empty-options configuration and sample revision. Every reuse also checks opaque EP compatibility metadata against the currently selected device, so an unchanged source hash does not bypass hardware/driver/runtime compatibility validation. A fresh compile uses its own generation directory; publication happens only after successful validation. The whole directory remains in place so external engine sidecars are not lost.

This is a small demonstration cache. It has no eviction, concurrent-process coordination, external-source-data hashing or integrity manifest for engine sidecars. Do not copy its index alone, move it between machines, edit generation files, or present it as Trackdub's production cache. Retain the original model. These are sample scope limits, not additional Microsoft platform requirements. [Source identity, compilation and artifact validation](https://learn.microsoft.com/windows/ai/new-windows-ml/model-compilation).

## Validation evidence

On 2026-10-10, Release build passed on .NET SDK 10.0.401 with zero warnings and errors. The no-argument usage path also ran; its exit code is intentionally 2. Subsequent runtime validation acquired the linked model, fixed dynamic-batch metadata handling and canonical EP-name lookup, and completed CPU, explicitly selected DirectML and TensorRT RTX inference. The DirectML cache path safely selected the original model. TensorRT RTX compilation reported `EP_SUPPORTED_OPTIMAL`; a subsequent process validated and reused the compiled artifact. See [runtime validation](runtime-validation.md) for exact scope, results and remaining gaps. For placement evidence beyond explicit session configuration, use [Windows ML logs](https://learn.microsoft.com/windows/ai/new-windows-ml/logs).
