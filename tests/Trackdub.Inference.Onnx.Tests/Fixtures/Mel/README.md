# Mel golden fixtures (M10)

`mel-goldens.json` contains spot log-mel values (mel bin, frame, value) captured from
the current verified-correct Whisper, Qwen (`QwenMelSpectrogramMath` via
`Qwen3AsrMelFeatureExtractor`), and Nemotron mel implementations.

Deterministic test signals (generated in code in `MelGoldenTests.cs`, not stored as
audio files): 1 s of silence, a 1 kHz sine (amplitude 0.5, 16 kHz), and a 100-4000 Hz
linear chirp. The golden file is a small text fixture, not recorded audio.

Regeneration is deliberate only:

```bash
GENERATE_MEL_GOLDENS=1 dotnet test tests/Trackdub.Inference.Onnx.Tests -f net10.0 --filter "FullyQualifiedName~MelGoldenGenerator" -m:1
# then copy the generated file from the test output Fixtures/Mel/ directory over this file
```

Analytic expectations in the tests (silence floors, HTK mel peak bins/centers,
frame counts, reflect padding) are computed independently and do not come from
this file.
