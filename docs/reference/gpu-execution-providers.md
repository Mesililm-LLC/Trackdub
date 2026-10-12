# GPU execution providers

How Trackdub selects ONNX Runtime execution providers, what each build can run,
and how to verify GPU execution end to end.

## Builds and provider reach

`Trackdub.Cli`, `Trackdub.Benchmarks`, and `Trackdub.Inference.Onnx` multi-target
on Windows:

| Target framework | Providers that can actually create sessions |
|------------------|---------------------------------------------|
| `net10.0` (portable, every OS) | CPU, **TensorRT RTX** (EP ABI plugin, Windows/Linux NVIDIA) |
| `net10.0-windows10.0.19041.0` | CPU, TensorRT RTX, **DirectML**, Windows ML catalog EPs (MIGraphX, OpenVINO catalog, QNN, VitisAI) |
| Linux `net10.0` | CPU, TensorRT RTX, native CUDA/TensorRT (system ORT GPU build), **OpenVINO** and **MIGraphX** (plugin EPs via [LinML.Catalog](https://github.com/trackdubllc/LinML)) |

The portable `net10.0` build compiles the Linux and macOS code paths on every build host and
picks the bootstrapper, device enumerator and native routes with `OperatingSystem.IsLinux()` /
`IsMacOS()` at run time, so the same artifact behaves the same whichever OS built it.

`OnnxRuntimeBuildCapabilities` encodes this per target framework. Provider
discovery consults it, so `trackdub providers list` on the portable build reports
DirectML and the Windows ML catalog EPs as unavailable instead of listing them
as selectable. Requesting an unrunnable provider prints a CLI warning naming the
required target; the request then falls through to the next provider.

### OpenVINO on Linux

`ExecutionProviderKind.OpenVino` on Linux runs on the OpenVINO plugin EP that
`LinML.Catalog` pins, installs, registers and appends — the Linux counterpart to
the Windows ML catalog's OpenVINO EP. Session bootstrap calls
`EnsureReadyAsync` (downloads allowed, bounded at 5 minutes; 15 seconds when only
registering), and the session appends the first NPU, GPU or CPU device the plugin
exposes (only the CPU device in OpenVINO CPU-proxy mode). Discovery never downloads:
it registers an installed plugin and reports OpenVINO available only when it exposes
a usable device. While any OpenVINO device is excluded for a run, the planner treats
OpenVINO as unavailable, because the catalog append cannot be pinned to a device
index.

Installing on Linux uses the same path as the Windows ML catalog EPs:
`OpenVinoCatalogReadinessProbe` reports the `LinMlCatalog` route, the runtime
readiness service offers the install while the plugin is missing or its download
failed, `StarterPackGpuSetupAdvisor` proposes `IntelOpenVino` on Intel hardware, and
`GpuRuntimeInstallOrchestrator` dispatches it to `OpenVinoCatalogEpInstaller`, which
downloads, verifies and registers the pinned plugin through LinML. On Linux these
offer and accept only `IntelOpenVino`, and only on hardware with an Intel CPU or an
Intel display/NPU PCI device, plus `AmdMigraphx` (see below); other runtime kinds stay Windows-only. The caller obtains Intel license consent first, as on Windows.
Opt-in check: `TRACKDUB_LINML_OPENVINO_INSTALL_E2E=1 dotnet test
tests/Trackdub.Composition.Tests -f net10.0 --filter Installer_end_to_end`. The standalone OpenVINO runtime component
(`libopenvino_c.so`) registers no ORT execution provider and no longer decides
OpenVINO readiness on Linux.

Opt-in end-to-end check on an Intel host:

```bash
TRACKDUB_LINML_OPENVINO_MODEL=/path/to/small-model.onnx \
  dotnet test tests/Trackdub.Inference.Tests -f net10.0 --filter LinuxOpenVinoSessionE2ETests
```

### MIGraphX on Linux

`ExecutionProviderKind.Migraphx` on Linux runs on AMD's MIGraphX plugin EP
(`onnxruntime-ep-amdgpu`), which LinML builds from a pinned commit, publishes as the
`ep-migraphx-<version>` release and pins by sha256 in `LinML.Catalog`. The host needs
an AMD GPU and ROCm with MIGraphX 2.17 or newer: a loadable `libmigraphx_c.so.3` under
`/opt/rocm/lib` or on the loader path that exports the 2.17 C API (MIGraphX 2.15 ships
the same soname without it). Without either, `LinMlMigraphxCatalog` reports
the provider unsupported and never downloads the plugin; the catalog itself has no
such check. Once registered, the plugin's GPU device is what the existing MIGraphX
session append uses, so sessions need no Linux-specific append code.

An ONNX Runtime build with MIGraphX compiled in (`MIGraphXExecutionProvider` in
`GetAvailableProviders`) still works and takes precedence, reported on the
`NativeRocm` route. Otherwise `MigraphxReadinessProbe` reports the `LinMlCatalog`
route: install is offered while the plugin is missing or its download failed,
`StarterPackGpuSetupAdvisor` proposes `AmdMigraphx` for AMD GPUs, and
`GpuRuntimeInstallOrchestrator` installs it by probing with downloads allowed, then
clears the cached readiness so the next check sees the installed plugin. The caller
obtains AMD license consent first, as on Windows. The Linux device enumerator lists
MIGraphX for an AMD GPU when the ROCm MIGraphX runtime is present. Not yet verified
on AMD hardware; on a host without ROCm, registration fails with
`libmigraphx_c.so.3: cannot open shared object file`, which is why the runtime is
checked first.

## Provider order

On Windows with an NVIDIA GPU the effective preference chain is:

```text
TensorRT RTX (EP ABI plugin) → DirectML → CPU
```

More precisely, `Milestone5PlanningPolicy.SupportedProvidersThisMilestone` orders
all providers (`TensorRTRtx → Migraphx → OpenVinoCatalog → Qnn → VitisAi →
TensorRt → Cuda → OpenVino → CoreMl → DirectMl → Dnnl → Cpu`), and each stage intersects
that with its own allow-list. The planner picks the first provider that is
discovered **and** passes the per-model smoke test; smoke failure falls through
to the next provider. Provider registration alone never counts as readiness.

## CLI controls

| Flag | Behavior |
|------|----------|
| `--prefer-gpu` | Soft preference: vendor EP first (Windows NVIDIA → trt-rtx, AMD → migraphx, Intel → openvino-catalog; Linux NVIDIA → cuda), then DirectML, then CPU. |
| `--require-gpu` | Same resolution but hard-requires the preferred EP where the stage allows it. |
| `--execution-provider <kind>` | Soft pin to a specific provider; falls through on family exclusion or smoke failure. Native `cuda`/`tensorrt` are Linux-only; on Windows they are rejected with remediation — use `trt-rtx` for NVIDIA acceleration. (The legacy SDK `ExecutionProviderPreference.Cuda` and starter-pack `cuda` tokens still map to `trt-rtx` on Windows.) |
| `--require-execution-provider` | Hard pin: no fallthrough when the stage allows the provider. |

## Engine-family exclusions

Some engine families can never see TensorRT providers because the combination is
broken at the native level, including failures that **kill the host process**
rather than throwing a catchable error. `StageRuntimeRequirementsCatalog` encodes
these as `AllowedProvidersByEngineFamily` overrides:

| Stage | Family | Reason |
|-------|--------|--------|
| ASR | `whisper-onnx` | Olive exports use contrib ops TRT RTX cannot import |
| ASR | `whisper-genai` | ORT GenAI `NvTensorRtRtx` can terminate the process |
| Translation | `opus-mt` | InferenceSession ctor stack overflow under TRT RTX |
| Translation | `madlad` | Not excluded: TRT RTX first (the `trt-fp16` variant), then CPU, then DirectML |
| Translation | `phi-genai` | Same GenAI crash class as `whisper-genai` |
| TextRefinement | `qwen-instruct`, `phi-genai` | Same GenAI crash class (observed on Qwen2.5-1.5B) |
| TTS | `kokoro` | CPU-only (ConvTranspose block) |
| TTS | `chatterbox`, `qwen3-tts` | Contrib ops / graphs TRT RTX cannot import |
| LipSynthesis | `latentsync-diffusion` | `MultiHeadAttention` TRT RTX cannot import |

MADLAD-400 3B runs on TRT RTX through its `trt-fp16` variant (the mixed fp16/fp32 export, the
only variant listing `trt-rtx`). TRT RTX compiles kernels for every new input shape, so the engine
pads encoder and decoder inputs to length buckets (16/32/64/128/256/512), re-runs the decoder prefix
each step, and reads the logits at the last real position; the sessions replay a CUDA graph per
bucket. On an RTX 5070 a warm one-segment clip translates in ~2.1 s versus ~7.9 s for the int4
KV-cache decoder on CPU, with output matching unpadded fp16 decoding. The sessions hold ~8 GB of
VRAM, and a failed TRT RTX session falls back to CPU unless the provider was hard-pinned.

CosyVoice is deliberately **not** excluded: its multi-graph package compiles under
TRT RTX, and two guards isolate per-graph failures — the per-graph
`TrtRtxUnsupportedOpScanner` skips graphs with unsupported contrib ops before any
session is built, and session-init fallback retries DirectML then CPU on TRT init
errors (disabled on hard-pin routes, where the TTS smoke instead proves every
CosyVoice graph up front).

Because a fatal crash cannot be caught and reported as a smoke failure, the
smoke tester also **refuses** the combinations outright before touching native
code: `ThrowIfGenAiTensorRtProvider` for ORT GenAI model loads, and
`ThrowIfFatalTensorRtFamily` for `opus-mt` encoder-decoder session construction. The smoke sweep bypasses stage allow-lists by design, so these
guards live at the point of danger; affected targets report `FAIL` in
`trackdub providers trt-rtx smoke` rather than attempting the load.

## Verifying GPU execution

```powershell
# Readiness probe (no session):
trackdub providers trt-rtx status

# Honest per-provider availability for the current build:
trackdub providers list

# Planner-style smoke across cached bundled models (incremental PASS/FAIL/SKIP
# lines; results are emitted per target so a mid-catalog crash loses nothing):
trackdub providers trt-rtx smoke
```

For real pipeline evidence, run a stage and check the logged selected provider,
not the readiness output. Registration ≠ model loaded ≠ ran ≠ succeeded.

## Resident serving (`trackdub serve`)

Each new process pays EP bootstrap, the model content hash and session/engine
construction again. `trackdub serve` pays them once and keeps the models pinned in
the session pool, then answers JSON-lines inference requests on stdin/stdout. The
command help (`trackdub serve --help`) documents the protocol.

```powershell
trackdub serve --model encoder=models\encoder.onnx --execution-provider trt-rtx `
  --warmup-inputs encoder=warmup.json
```

- Any provider works. `--execution-provider` picks it for the `--model` list, and
  `auto` takes the first discovered provider in the order above. A `load` request
  can add more models on other providers at runtime.
- With `--require-execution-provider`, a provider that cannot load the model fails
  with `provider-unavailable`. Otherwise it falls back to CPU, and the fallback
  shows in the `fallbackChain` of `health` and in the stderr diagnostics.
- Sessions open through the same factory as pipeline stages. EP-context artifacts,
  the TensorRT RTX runtime cache and pool admission budgets therefore all apply.
- Each model gets a warmup run before serving. `--warmup-inputs` is fail-fast.
  Without it, the server runs zero tensors (dynamic dims = 1) and only warns on
  failure; `--no-warmup` skips this.
- A failed run marks the model poisoned. The next request evicts the session and
  reopens it.
- `--ecoqos` opts the serve process into Windows EcoQoS at startup (see the
  efficiency knobs below); `health` reports the verified state as `ecoQos`.

## Efficiency knobs

Windows ML efficiency guidance maps onto three opt-in mechanisms:

| Knob | Scope | Effect |
| --- | --- | --- |
| Windows ML device policy `max-efficiency` / `min-overall-power` / `min-power` | Session device selection **and** NPU workload hint | Selects efficiency-oriented devices and, for NPU catalog sessions (QNN / VitisAI / OpenVINO), sets `ep.dynamic.workload_type=Efficient` through ONNX Runtime (`SetEpDynamicOptions`). Outcomes are logged; the mode is never silently claimed. |
| `TRACKDUB_ECOQOS=1` | Inference worker process (inherited from host env) | Opts the out-of-process worker into Windows EcoQoS power throttling (CPU efficiency scheduling). Background/batch usage; leave off for latency-critical runs. |
| `TRACKDUB_DISABLE_CPU_MEM_ARENA=1` | All ORT sessions in the process | Disables the CPU memory arena (system allocator instead of the ORT arena): lower peak memory, throughput cost. Deployment opt-in for memory-constrained hosts. |

Quantized model variants (int8 / int4 / fp16) are selected per stage by the runtime planner
already; see "Provider order". A `trackdub serve` process can additionally opt in with
`--ecoqos`.

## References

- [tensorrt-rtx-ep-abi-plugin.md](tensorrt-rtx-ep-abi-plugin.md): plugin bundle, install, license, smoke
- [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md): per-stage allow-lists and smoke history
- [ADR-0002](../decisions/ADR-0002-windows-ml-provider-strategy.md): provider strategy
