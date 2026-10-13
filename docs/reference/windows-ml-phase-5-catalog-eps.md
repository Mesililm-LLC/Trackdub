# Windows ML Phase 5: catalog EP expansion (OpenVINO, QNN)

Internal checklist for Intel OpenVINO, Qualcomm QNN, AMD MIGraphX, and AMD VitisAI **Windows ML catalog** routes. TensorRT RTX is not a Windows ML catalog route anymore; it uses the standalone ORT EP ABI plugin documented in [tensorrt-rtx-ep-abi-plugin.md](tensorrt-rtx-ep-abi-plugin.md).

## Relationship to existing paths

| Path | Enum | When used |
|------|------|-----------|
| Standalone OpenVINO | `ExecutionProviderKind.OpenVino` | Linux; optional Windows install via component downloader (`Infrastructure`). |
| WinML catalog OpenVINO | `ExecutionProviderKind.OpenVinoCatalog` | Windows catalog append (`TryAppendOpenVinoCatalogProvider`); **not** the same as standalone OpenVINO. |
| WinML catalog QNN | `ExecutionProviderKind.Qnn` | Snapdragon / NPU catalog EP on Windows (`TryAppendQnnProvider`, NPU then GPU device preference). Falls back to CPU if append fails. |

Do not duplicate “GPU ready” semantics between standalone OpenVINO install state and catalog registration.

## Code seams

| Concern | Location |
|---------|----------|
| Catalog provider name constants | `Trackdub.Inference.Onnx/WindowsMl/WindowsMlCatalogProviderIds.cs` |
| Registration / bootstrap | `WindowsMlProviderRegistrationPolicy.cs`, `WindowsMlExecutionProviderBootstrapper.Windows.cs` |
| Discovery | `OnnxExecutionProviderDiscovery.cs` — QNN uses `QnnCatalogReadinessProbe`; reports unavailable until the catalog EP is registered |
| Session append | `OnnxExecutionSessionFactory.cs` `CreateQnnSelection` → `WinMlCatalogSessionOptionsExtensions.AppendQnnOrFallback` (`src/Trackdub.Inference.Onnx/WinMlCatalog/`) |
| Milestone probe order | `StageRuntimeRequirements.cs` — **unchanged in 5c** |

Stub marker: `#TODO(phase-5-catalog-ep)` in code; reference this doc and [ADR-0002 Phase 5](../decisions/ADR-0002-windows-ml-provider-strategy.md).

## Suggested smoke commands (when hardware exists)

Windows TFM:

```powershell
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --help
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --model <model-id> --provider dml
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --model <model-id> --windows-ml-device-policy PreferNpu
```

For TRT RTX smoke commands, use the plugin doc instead of this catalog checklist.

## QNN on a Snapdragon (Windows on ARM) host

The Snapdragon box is `win-arm64`. Build or publish with `-r win-arm64` so the Windows ML native DLLs (`onnxruntime.dll`, `Microsoft.Windows.AI.MachineLearning.dll`, `DirectML.dll`) come from `runtimes/win-arm64`; a build with no runtime identifier puts x64 binaries at the output root. A cross-build from an x64 PC works (verified: the `win-arm64` output has ARM64 natives), but nothing here has been run on Snapdragon hardware.

1. **EP only, no Trackdub (seconds).** `tools/provider-smoke/Run-QnnSmoke.ps1` sets up an ARM64 Python venv with `onnxruntime-qnn`, generates a tiny model, and runs `cpu,qnn`. This tests the ORT QNN wheel and the NPU/driver, not Trackdub's catalog path. See `tools/provider-smoke/README.md` for `ok` / `fallback` / `fail` / `absent`.
2. **Readiness and install through Trackdub.**

   ```powershell
   trackdub providers qnn status
   trackdub providers qnn install --accept-license
   trackdub providers qnn status
   ```

   `install` refuses without the Qualcomm license flag (persisted in studio settings, same pattern as `providers trt-rtx install`). `status` exits non-zero until the catalog EP is registered.
3. **Benchmark through Trackdub's QNN route.**

   ```powershell
   dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -r win-arm64 -- --model <model-id> --provider qnn
   ```

   `--provider qnn` is an explicit route (`CreateQnnSession` → `AppendQnnOrFallback`) and fails rather than silently falling back to CPU when QNN is not visible to ONNX Runtime. The benchmark host does not download the EP; run step 2 first.

Record the **actual EP** reported by the run in [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md) and the Snapdragon matrix row below.

## Matrix rows to add (when hardware exists)

| Hardware | Stage | Catalog EP to exercise | Notes |
|----------|-------|------------------------|-------|
| Intel GPU box | VAD / ASR | OpenVINO catalog | Distinct from standalone OpenVINO row |
| Snapdragon / Copilot+ PC | VAD / ASR | QNN | Pair with `PreferNpu` policy smoke; mark N/A if no NPU |

Update [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md) with pass/fail and **actual EP** from console — never infer from policy name alone.

## Enablement order (post-5c)

1. Confirm catalog ids in `WindowsMlCatalogProviderIds`.
2. Implement session append + smoke tester path.
3. Run stage matrix smoke on target hardware.
4. Update manifest `expected_runtime` and stage allow-lists if product commits.
5. Only then extend `Milestone5PlanningPolicy.SupportedProvidersThisMilestone`.

## References

- [ADR-0002](../decisions/ADR-0002-windows-ml-provider-strategy.md)
- [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md)
- [windows-ml-phase-4-closeout.md](windows-ml-phase-4-closeout.md)
- [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md)
