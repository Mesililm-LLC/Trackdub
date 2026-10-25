# Maxine LipSync persistent host

`trackdub-maxine-lipsync-host` keeps one NVIDIA Maxine AR SDK LipSync session alive across turns, so
Trackdub can stream raw frames through it instead of launching the SDK's `LipSyncApp` sample for every
turn. Nothing from the AR SDK (headers, proxy sources, binaries, models) is checked in here; the build
compiles against a local SDK install.

## Build (Windows)

From a Visual Studio x64 developer prompt:

```bat
cmake -S tools\native\maxine-lipsync-host -B build\maxine-lipsync-host -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release -DARSDK_ROOT=D:/path/to/ARSDK
cmake --build build\maxine-lipsync-host
```

`ARSDK_ROOT` (or the `TRACKDUB_MAXINE_ARSDK_ROOT` environment variable) must point at the SDK folder
containing `nvar/include`, `nvar/src`, `bin` and `features/nvarlipsync`.

## Run

The runtime needs `NV_AR_SDK_PATH=USE_APP_PATH` and `PATH` covering `<ARSDK>\bin` plus every
`<ARSDK>\features\*\bin`. Pass the model folder with `--model_path=<ARSDK>\bin\models`.

`--protocol` prints the handshake line and exits; Trackdub uses it to detect the host.

## Protocol (stdin/stdout, binary)

1. On start the host writes `TRACKDUB-MAXINE-LIPSYNC 1 READY\n`.
2. Each job is a header line `RUN <width> <height> <fps> <language> <frames> <wav-path>\n` followed by
   `frames` raw BGR24 frames. The WAV must be 16 kHz mono PCM16.
3. The host replies with exactly `frames` BGR24 frames (the tail is padded with the last output frame
   to cover the model's start-up latency), then `OK <frames> <milliseconds>\n` or `ERR <message>\n`.
4. `QUIT\n` ends the session. Diagnostics go to stderr.

Write input and read output concurrently: the model holds back `NumInitialFrames` (40 for the Spanish
model at 30 fps) before its first output frame, so a writer that waits for output deadlocks.

## Known limits

- `NvAR_ResetState` is not implemented for LipSync in AR SDK 1.2.0.0, so the host reloads the feature
  for every job (about 5 s on an RTX 5070) to keep turns from bleeding into each other.
- RTX 50 series (compute capability 12.0) needs the SDK's sm100 Windows model build.
