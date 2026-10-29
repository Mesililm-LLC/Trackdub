# Operational & Environment Reference

Detailed operational facts and environment-specific instructions for Trackdub public core (`Mesililm-LLC/Trackdub`).

## Cloud & Headless Environment
- No long-running services. Primary entrypoint is headless CLI (`src/Trackdub.Cli`).
- Always pass `-m:1` to `dotnet restore`, `dotnet build`, and `dotnet test`.
- NuGet feeds: `nuget.org` and Azure Artifacts `dotnet-libraries` feed (for `Microsoft.ML.Tokenizers` 3.x preview).
- Verification gate: `dotnet format Trackdub.slnx --verify-no-changes`.
- Media stages require FFmpeg/ffprobe; verify with `dotnet run --project src/Trackdub.Cli -- doctor`.
- Model cache downloads: `dotnet run --project src/Trackdub.Cli -- models bundle-needed`, then `dotnet run --project src/Trackdub.Cli -- models download <id>`. Tests skip cleanly when models are absent.
- Default data and cache root paths land under `~/.local/share/Trackdub` (override via `TRACKDUB_DATA_ROOT` / `TRACKDUB_CACHE_ROOT`).

## Execution Providers
- GPU provider routing: TRT RTX -> DirectML -> CPU. See `docs/reference/gpu-execution-providers.md`.
- TensorRT RTX uses standalone ORT EP ABI plugin. See `docs/reference/tensorrt-rtx-ep-abi-plugin.md`.
- Windows ML Phase 5 catalog routes: see `docs/reference/windows-ml-phase-5-catalog-eps.md`.
