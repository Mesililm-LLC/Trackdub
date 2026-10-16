# ResNet runtime validation

Runtime observations from running this sample are recorded in the shared Tier 3 evidence store: see [observations.md](../../trackdub/observations.md) entries **O-8 through O-16**. That file defines the required fields (observer, reproduction count, cause confidence, status, acted on in) and the re-test obligations for each entry.

## Summary

Observations cover:

- **O-8:** dynamic-batch rejection fixed in the shape guard; fix confirmed.
- **O-9:** CPU baseline (exit 0, top-five scores noted).
- **O-10:** DirectML inference; scores matched CPU.
- **O-11:** DirectML with `--cache`; original model selected (no artifact promoted).
- **O-12:** TensorRT RTX catalog state during and after acquisition.
- **O-13:** TensorRT RTX original-model inference after catalog preparation.
- **O-14:** Canonical EP name fix for cache lookup; compilation reported `EP_SUPPORTED_OPTIMAL`. Fix confirmed.
- **O-15:** TensorRT RTX cache reuse across processes; reuse confirmed.
- **O-16:** PR #484 Release build, zero warnings; eight shape-guard cases verified.

No semantic labels were evaluated. ETW/operator placement, GPU utilization and latency benchmarks were not captured. OpenVINO, QNN, VitisAI, MIGraphX and experimental WebGPU were not run. These observations do not qualify any Trackdub pipeline stage. The original model and raw console logs remain in a local run directory outside the repository. For stronger placement evidence, use the [Windows ML logging procedure](https://learn.microsoft.com/windows/ai/new-windows-ml/logs).
