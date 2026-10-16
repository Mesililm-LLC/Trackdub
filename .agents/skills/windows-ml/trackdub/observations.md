# Trackdub observations

Tier 3. These are things seen on Trackdub machines. None is a platform fact: some may come from Trackdub's own configuration, export choices or bugs. Treat each as a lead to re-test. Before code relies on one, reproduce it under the current versions and update its entry.

## Entry format

Every entry records:
- **Observed:** date, and the agent or person.
- **Setup:** OS, GPU and driver, package versions, model or export, how it was run.
- **Result:** what was seen, quoting the decisive line.
- **Reproduced:** how many times, under which variations.
- **Cause confidence:**
  - *confirmed*: cause found and fix verified;
  - *suspected*: plausible cause, not isolated;
  - *unknown*: not investigated;
  - *contradicted*: a higher tier disagrees;
  - *not applicable*: the entry records a state, not a failure.
- **Status:**
  - *current*: still relied on;
  - *needs re-test*: cause not settled, or versions moved on;
  - *misconfiguration*: Trackdub's own setup caused it;
  - *superseded*: no longer applies.
- **Acted on in:** files or PRs that encode a decision based on it, or "nothing".

Every field is required; write "unknown" or "nothing" rather than leaving one out. Add entries; do not rewrite history. When re-testing, append a dated result to the entry.

---

### O-1. A foreign `onnxruntime.dll` removed DirectML from the desktop

- **Observed:** 2026-10-07, Claude Code session.
- **Setup:**
  - OS and GPU: Windows 11 Pro 10.0.26200; NVIDIA GeForce RTX 5070, driver 617.42 as read on 2026-10-10 (the driver at observation time was not recorded).
  - Packages: the Windows App SDK 1.8-era Windows ML packages (exact version unknown) plus stock ORT 1.30 from the desktop build.
  - Model or export: every DirectML stage, not model-specific.
  - How run: Desktop app (Trackdub-gated). A build target, `AlignOnnxRuntimeNativeForGenAi`, copied stock ORT 1.30 over Windows ML's `onnxruntime.dll`.
- **Result:** Every DirectML stage silently ran on CPU. `GetEpDevices()` listed no `DmlExecutionProvider`.
- **Reproduced:** Yes. The fix (moving GenAI to `.WinML` and deleting the target) restored DirectML, measured end to end.
- **Cause confidence:** confirmed.
- **Status:** misconfiguration (Trackdub build step), now fixed.
- **Acted on in:** #419; Trackdub-gated branch `feat/winml-2.4-desktop`.
- **Lesson worth keeping:** any build step that copies `onnxruntime*.dll` into an output folder can replace Windows ML's runtime. Learn's warning against mixing GenAI flavors (Tier 1) points the same way.

### O-2. GenAI Whisper and Qwen2.5 failed on DirectML

- **Observed:** 2026-10-07, Claude Code session.
- **Setup:**
  - OS and GPU: Windows 11 Pro 10.0.26200; NVIDIA GeForce RTX 5070, driver 617.42 as read on 2026-10-10 (the driver at observation time was not recorded).
  - Packages: Windows ML 2.4.89 (ORT 1.27.1), GenAI.WinML 0.17.1.
  - Model or export: Trackdub's bundled Whisper and Qwen2.5 GenAI exports, which were built for CPU/CUDA, not DirectML.
  - How run: a standalone probe in `D:\tdhost\gp` that loads the model and generates.
- **Result:**
  - Whisper ended the host process inside `Generator.SetInputs`.
  - Qwen2.5 failed its first DirectML kernel with `DmlFusedNode` 0x80070057.
- **Reproduced:** On one machine, one export each. Not tried with DirectML-targeted exports or other drivers.
- **Cause confidence:** suspected. The exports were never built for DirectML, so this may say more about the exports than about GenAI on DirectML.
- **Status:** needs re-test before relaxing the exclusion.
- **Acted on in:** `GenAiProviders` in `StageRuntimeRequirements.cs`; the DirectML refusal in `OnnxExecutionProviderSmokeTester.cs`.

### O-3. Only the uppercase `DML` provider spelling took effect in GenAI

- **Observed:** 2026-10-07, Claude Code session.
- **Setup:** Same probe as O-2. `config.SetProviderOption("DML", "enable_graph_capture", "0")` versus lower-case `dml`.
- **Result:** The option appeared to take effect only with `DML`.
- **Reproduced:** Once.
- **Cause confidence:** unknown. GenAI main normalizes provider names case-insensitively ([upstream](../upstream/ort-and-genai-source.md)); 0.17.1's behaviour is unverified. The difference may have come from another variable in the probe.
- **Status:** needs re-test. Do not encode casing rules from this.
- **Acted on in:** nothing in code.

### O-4. GenAI on NvTensorRtRtx ended the host process

- **Observed:** before 2026-10-09 (recorded in the smoke tester's message), observer not recorded.
- **Setup:**
  - OS and GPU: unknown.
  - Packages: unknown.
  - Model or export: Trackdub's bundled GenAI models.
  - How run: GenAI model load on the TRT-RTX EP; the exact route is unknown.
- **Result:** A native stack overflow ended the host process.
- **Reproduced:** Unknown.
- **Cause confidence:** unknown.
- **Status:** needs re-test.
- **Acted on in:** `WithoutTensorRtFamilies` for GenAI in `StageRuntimeRequirements.cs`; the TRT refusal in `OnnxExecutionProviderSmokeTester.cs`.

### O-5. Full pipeline completed on Windows ML 2.4

- **Observed:** 2026-10-07, Claude Code session. Evidence lives in a local run folder, not in the repository.
- **Setup:**
  - OS and GPU: Windows 11 Pro 10.0.26200; NVIDIA GeForce RTX 5070, driver 617.42 as read on 2026-10-10 (the driver at observation time was not recorded).
  - Packages: Windows ML 2.4.89 (ORT 1.27.1), GenAI.WinML 0.17.1, after the DirectML VRAM factor change (#418).
  - Model or export: Trackdub's default per-stage models as planned at the time.
  - How run: a full dubbing pipeline run on a test clip, with no provider overrides.
- **Result:**

  | Stage | Provider |
  | --- | --- |
  | VAD | DirectML |
  | Diarization | TRT-RTX |
  | ASR (qwen3) | DirectML |
  | GenAI | CPU |
  | Translation | CPU |
  | TTS | CPU |

  The run completed with no overrides.
- **Reproduced:** Once.
- **Cause confidence:** not applicable.
- **Status:** needs re-test. It goes stale as soon as pins, the planner or exports change, so re-run before quoting it.
- **Acted on in:** nothing; it was used as evidence in #419's description.

### O-6. Managed ORT 1.30 runs against native ORT 1.27.1 on Windows

- **Observed:** 2026-10-09, from the build configuration, not from a failure.
- **Setup:** On the Windows TFM, `Microsoft.ML.OnnxRuntime.Gpu` is referenced managed-only, with `ExcludeAssets="native"`; the native ORT is Windows ML's.
- **Result:** The pipeline works (O-5). No crash has been attributed to the version gap.
- **Reproduced:** not applicable; this is a configuration fact.
- **Cause confidence:** not applicable. The risk is explained in [upstream](../upstream/ort-and-genai-source.md). Nobody has inspected the output folder to confirm which managed copy ships ([upstream/packages.md](../upstream/packages.md)).
- **Status:** superseded on 2026-10-10. The Windows host project no longer references stock managed ORT. Its output `Microsoft.ML.OnnxRuntime.dll` matched the Windows ML 2.4.89 package copy, and its `onnxruntime.dll` matched that package's win-x64 native. The CUDA worker output matched stock managed ORT 1.30 and `Microsoft.ML.OnnxRuntime.Gpu.Windows` 1.30 win-x64 native, under `inference-worker\` only. This was a file-hash check of a Debug build, not a model run.
- **Acted on in:** `src/Trackdub.Inference.Onnx/Trackdub.Inference.Onnx.csproj` (Windows ML managed binding on the host; stock managed ORT only for the worker host compile) and `src/Trackdub.InferenceWorker/Trackdub.InferenceWorker.csproj`.

### O-7. Deep scratch paths broke native DLL loading

- **Observed:** 2026-10-07, Claude Code session.
- **Setup:**
  - OS and GPU: Windows 11 Pro 10.0.26200; NVIDIA GeForce RTX 5070, driver 617.42 as read on 2026-10-10 (the driver at observation time was not recorded).
  - Packages: Windows ML 2.4.89, GenAI.WinML 0.17.1.
  - Model or export: not model-specific.
  - How run: probe executables built and run under a long temp path.
- **Result:** The native DLL loader failed. The same probe moved to `D:\tdhost\...` worked.
- **Reproduced:** Once.
- **Cause confidence:** suspected (path length).
- **Status:** current, as a practical tip only.
- **Acted on in:** nothing in code; probes since then have lived under `D:\tdhost\`.

### O-8. ResNet dynamic-batch rejection on the standalone sample

- **Observed:** 2026-10-10, Codex agent.
- **Setup:**
  - OS and GPU: Windows 11 Pro, build 26200; NVIDIA GeForce RTX 5070, driver 32.0.16.1742.
  - Packages: Windows ML 2.4.89; System.Drawing.Common 8.0.31; .NET SDK 10.0.401 / runtime 8.0.31.
  - Model or export: `resnet50-v2-7.onnx` (SHA-256 `79102261…F31`, 102 MB) from the Microsoft Learn walkthrough.
  - How run: standalone ResNetWalkthrough sample, `.agents/skills/windows-ml/samples/ResNetWalkthrough/`.
- **Result:** The sample's shape guard rejected the model because it declared a dynamic batch dimension (`[-1,3,224,224]` and `[-1,1000]`). The guard previously required the batch dimension to be exactly `1`.
- **Reproduced:** Once. The fix was verified by the same agent on the same machine.
- **Cause confidence:** confirmed. Guard fixed to accept `1` or `-1` for the batch position while rejecting any other value and preserving strict rank, spatial and output-shape checks. Binding a fixed batch-1 tensor against a model that declares dynamic batch is valid ONNX semantics.
- **Status:** current.
- **Acted on in:** `MatchesSingleImageShape` in `Program.cs` (PR #484).

### O-9. ResNet CPU inference, three executions

- **Observed:** 2026-10-10, Codex agent.
- **Setup:** Same as O-8. Explicit `CPUExecutionProvider`, CPU device 0.
- **Result:** Exit 0. Inference returned 1000 finite scores and top-five output:
  ```
  Class index 644: 28.9243%
  Class index 604: 11.6401%
  Class index 499: 5.8384%
  Class index 714: 4.7572%
  Class index 111: 2.7703%
  ```
- **Reproduced:** Once with this model/image combination. No labeled accuracy evaluation.
- **Cause confidence:** not applicable.
- **Status:** current, as a baseline for comparison only.
- **Acted on in:** nothing.

### O-10. ResNet DirectML inference

- **Observed:** 2026-10-10, Codex agent.
- **Setup:** Same as O-8. Explicit `DmlExecutionProvider`, GPU device 0.
- **Result:** Exit 0. Top-five output matched CPU scores to four decimal places (same values as O-9). No per-node placement trace was captured.
- **Reproduced:** Once.
- **Cause confidence:** not applicable.
- **Status:** current.
- **Acted on in:** nothing.

### O-11. ResNet DirectML with --cache selected the original model

- **Observed:** 2026-10-10, Codex agent.
- **Setup:** Same as O-8. `DmlExecutionProvider`, `--cache`.
- **Result:** Exit 0. Logged `Using original model; no unvalidated compiled artifact was selected.` Compiled-artifact promotion and reuse were not exercised on this path.
- **Reproduced:** Once.
- **Cause confidence:** not applicable.
- **Status:** current.
- **Acted on in:** nothing.

### O-12. TensorRT RTX catalog state during acquisition

- **Observed:** 2026-10-10, Codex agent.
- **Setup:** Same as O-8. `NvTensorRtRtxExecutionProvider` without `--download`.
- **Result:** Exit 3; catalog reported `NotPresent`. A concurrent process run during an active download by another process also reported `NotPresent`, even though `Get-AppxPackage` already listed the EP package. The sample stopped without silently substituting another provider.
- **Reproduced:** Once per variant.
- **Cause confidence:** not applicable (behaviour matches documented install states). The intermediate `NotPresent` during download is unexpected but not investigated.
- **Status:** needs re-test if the catalog readiness API changes.
- **Acted on in:** nothing.

### O-13. TensorRT RTX catalog preparation and original-model inference

- **Observed:** 2026-10-10, Codex agent.
- **Setup:** Same as O-8. `NvTensorRtRtxExecutionProvider`, `--download`, after `Get-AppxPackage` confirmed both `Microsoft.WinML.NVIDIA.TRT-RTX.EP.2` and `Microsoft.WinML.NVIDIA.TRT-RTX.EP.Framework.2` version `2.30.49.0`.
- **Result:** Exit 0 after acquisition (~4 min). Dynamic-shape profile warnings appeared in native diagnostics; no undocumented options were added. Uncached run top-five class indices matched O-9/O-10; highest score was 29.0548%.
- **Reproduced:** Once.
- **Cause confidence:** not applicable.
- **Status:** current.
- **Acted on in:** nothing.

### O-14. TensorRT RTX canonical EP name for cache lookup

- **Observed:** 2026-10-10, Codex agent.
- **Setup:** Same as O-8. `NvTensorRtRtxExecutionProvider`, `--download`, `--cache`.
- **Result:** The device reported `NvTensorRTRTXExecutionProvider` (different casing from the CLI argument). Using the CLI spelling in compatibility metadata returned no usable metadata. Switching to the device's `EpName` property caused compilation to report `EP_SUPPORTED_OPTIMAL`.
- **Reproduced:** Once.
- **Cause confidence:** confirmed for this provider/version. Not generalised to every provider.
- **Status:** current.
- **Acted on in:** `GetValidatedModelPath` and `IsOptimal` in `Program.cs` (PR #484). The `epName` parameter now uses `selected[0].EpName` rather than the command-line selection string.

### O-15. TensorRT RTX cache reuse across processes

- **Observed:** 2026-10-10, Codex agent. Subsequent to O-14 (compilation published the cache index).
- **Setup:** Same as O-8. New process, `NvTensorRtRtxExecutionProvider`, `--cache` (no `--download`).
- **Result:** Exit 0. Logged `Reusing source-matched, EP-validated compiled model.` Top-five output:
  ```
  Class index 644: 29.2965%
  Class index 604: 11.6305%
  Class index 499: 5.7555%
  Class index 714: 4.6999%
  Class index 111: 2.7831%
  ```
  Scores differed from O-9/O-10 and from the O-13 uncached run; top-five indices were the same across all.
- **Reproduced:** Once.
- **Cause confidence:** not applicable. Score variation across EP backends and engine builds is expected.
- **Status:** current.
- **Acted on in:** nothing.

### O-16. PR #484 Release build, zero warnings

- **Observed:** 2026-10-15, Codex agent.
- **Setup:** Same as O-8 (OS/GPU/packages). Rebuilt the isolated sample from `main` at the PR #484 commit, Release, warnings-as-errors.
- **Result:** Zero warnings, zero errors. Eight shape-guard cases passed: fixed and dynamic input batch accepted; batch 2, a dynamic spatial dimension and wrong rank rejected; fixed and dynamic output batch accepted; batch 0 rejected. No-argument usage returned exit code 2.
- **Reproduced:** Once.
- **Cause confidence:** not applicable.
- **Status:** current.
- **Acted on in:** nothing (verification pass, not a code change).
