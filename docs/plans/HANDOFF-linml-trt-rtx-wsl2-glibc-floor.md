# Handoff: rebuild the patched WSL2 TensorRT-RTX plugin against an older glibc

**For:** an agent working in `trackdubllc/LinML` (branch from `main`, current release v0.5.0).
**Follows:** `HANDOFF-linml-wsl2-trt-rtx-catalog-support.md` (done: LinML PR #31, released in 0.4.2).
**Severity:** blocks the patched build on every WSL2 distro and container older than the machine that built it, including
Docker Desktop containers on Ubuntu 24.04.

## 1. Problem

LinML 0.4.2+ downloads and registers the patched TensorRT-RTX build on WSL2 correctly, but the released artifact does not load on
Ubuntu 24.04. Reproduced in Trackdub's Docker image (Ubuntu 24.04.5, glibc 2.39, `/dev/dxg` present) with `LinML.Catalog` 0.5.0:

```
Registering /home/trackdub/.local/linml-deps/ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep/libonnxruntime_providers_nv_tensorrt_rtx.so failed:
[ErrorCode:Fail] Failed to load library ... with error: /usr/lib/x86_64-linux-gnu/libm.so.6: version `GLIBC_2.43' not found
```

The catalog flow itself behaved as designed (installed stock runtime libraries, then the patched build, never registered stock).
The failure is the binary's glibc floor.

## 2. Evidence

Release `ep-trt-rtx-wsl2-0.4.0-wsl2p1`, asset `linml-trt-rtx-ep-0.4.0-wsl2p1-linux-x64.zip`
(sha256 `e87adedba3dfc86ea96cd7c6f5b9a965de584aeda971ee7792ae6d1e0074d2df`), library
`linml_trt_rtx_ep/libonnxruntime_providers_nv_tensorrt_rtx.so`:

| Symbol family | Highest version required | Notes |
|---|---|---|
| `GLIBC_` | **2.43** (also 2.38, 2.34, 2.33) | The stock NVIDIA wheel is `manylinux_2_35`; Ubuntu 22.04 has 2.35, 24.04 has 2.39 |
| `GLIBCXX_` | 3.4.32 | Ubuntu 22.04's libstdc++ tops out at 3.4.30 |
| `CXXABI_` | 1.3.15 | |

It was built locally (`scripts/build-trt-rtx-ep.sh`) on a host with a very new glibc, so it only loads on equally new distributions.
It worked in earlier WSL2 testing only because that distro was just as new. Reproduce the symbol check with:

```bash
objdump -T libonnxruntime_providers_nv_tensorrt_rtx.so | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -3
objdump -T libonnxruntime_providers_nv_tensorrt_rtx.so | grep -o 'GLIBCXX_[0-9.]*' | sort -Vu | tail -2
```

## 3. Required change

1. **Build in an old-glibc environment.** Run `scripts/build-trt-rtx-ep.sh` inside an `ubuntu:22.04` container (glibc 2.35, matching the stock wheel's
   `manylinux_2_35` floor), with a toolchain that runs there. To also clear the `GLIBCXX` floor either use that distro's own compiler or link
   `-static-libstdc++ -static-libgcc` (the plugin must not export or depend on a newer libstdc++ than 22.04 provides, 3.4.30).
   The TensorRT-RTX SDK (headers + import libs, 1.6.1.120) is not redistributable: it is supplied through `TRT_RTX_SDK` and must be mounted into the container.
   CUDA 13 headers are needed as before (`CUDA_ROOT`).
2. **Publish a new immutable release.** `EP_VERSION` must change, since published archives are pinned by sha256 and never overwritten:
   suggest `0.4.0-wsl2p2`, tag `ep-trt-rtx-wsl2-0.4.0-wsl2p2`. Keep the same zip layout (`linml_trt_rtx_ep/`, `build-info.json`, `PATCH-NOTICE.md`, `licenses/`)
   and install path `ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep/`.
3. **Update every pin together** (the `ScriptPinAgreementTests` test enforces agreement): `PluginPackageSpec.TensorrtRtxWsl2` (URL, sha256, version),
   `scripts/fetch-ep-plugins.sh` `fetch_trt_rtx_wsl2`, `scripts/build-trt-rtx-ep.sh` `EP_VERSION`, `build-info.json`, docs and changelog.
   Existing installs of `0.4.0-wsl2p1` must be replaced: check how the installer treats a differently versioned install under the same
   subdirectory, and make sure a host that already has `p1` vendored gets `p2` (otherwise the broken library keeps being used).
4. **Add a glibc/libstdc++ floor gate to the build.** After building, fail `build-trt-rtx-ep.sh` if the `.so` requires `GLIBC_` newer than 2.35 or `GLIBCXX_`
   newer than 3.4.30 (print the offending symbols). Add a unit/CI test over the published pin if feasible (download or fixture), so a rebuild on a new host cannot regress.
5. **Release.** Suggest **0.5.1** (pin change only, no API change); `CHANGELOG.md` entry; publish `LinML.Catalog` and `LinML.Contracts` to nuget.org and GitHub Packages
   (`release.yml` does both).

## 4. Acceptance criteria

1. `objdump` on the new library shows `GLIBC_` max <= 2.35 (and `GLIBCXX_` max <= 3.4.30).
2. In a fresh `ubuntu:24.04` container (`--gpus all`, Docker Desktop on Windows) and in an `ubuntu:22.04` container, with the stock wheel and the new build vendored,
   Two explicit calls to `ExecutionProviderCatalog.GetDefault().Find(ProviderId.TensorrtRtx)!.EnsureReadyAsync(new EnsureOptions(true))` return `Downloaded` and then `Ready`, respectively, with
   `Devices.Count >= 1`, and `tests/fixtures/model.onnx` returns `[11, 22, 13, 24, 15, 26]`. No restart, no env vars.
3. A host that previously installed `0.4.0-wsl2p1` ends up using `p2`.
4. `ScriptPinAgreementTests` and the rest of the suite stay green; bare-metal and non-WSL behaviour is unchanged.

## 5. Pitfalls

- Static `libstdc++` is fine for the plugin but check it does not duplicate ONNX Runtime's own libstdc++ symbols in a way that breaks exception handling across the plugin boundary; prefer the older-distro build if in doubt.
- The patched build still `NEEDED`s `libcudart.so.13`, `libtensorrt_rtx.so.1`, `libtensorrt_onnxparser_rtx.so.1` (from the stock wheel directory, preloaded by the catalog) and the system
  `libcuda.so.1` / `libnvml`. In Docker Desktop `libcuda.so.1` lives under `/usr/lib/x86_64-linux-gnu` (injected by the NVIDIA runtime), not `/usr/lib/wsl/lib`.
- Do not change `IExecutionProvider`, `EnsureOptions` or `ProviderEnsureResult` shapes (Trackdub implements fakes of them).
- The build is from a fork at upstream PR #12's head; keep the removal path (bump the stock pin and drop the variant when NVIDIA ships the fix).

## 6. Trackdub follow-up (not in the LinML repo)

- Bump `LinML.Catalog` to the new release in `Directory.Packages.props`, regenerate lock files (keep only semantically changed ones; the repo's CRLF normalization makes the rest noise).
- Re-run the Docker check: `docker compose run --rm -e TRACKDUB_ACCEPT_NVIDIA_TENSORRT_RTX_LICENSE=1 trackdub providers trt-rtx install`, then `providers trt-rtx smoke`.
- Delete `LinMlWsl2TensorRtRtxPlugin` and its wiring and tests once verified on a real WSL2 host with the new LinML version.
