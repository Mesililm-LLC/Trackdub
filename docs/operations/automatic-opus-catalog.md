# Automatic OPUS-MT catalog

The `opus-mt-publishers-v1` policy trusts the exact Hugging Face publishers
`onnx-community` and `Xenova` for compatible Marian OPUS-MT exports. This is
family-level approval: adding a language pair does not require a new app release
or an individually maintained bundled manifest entry.

Publisher membership alone does not establish commercial eligibility or runtime
readiness. Admission requires a supported encoder/decoder and tokenizer layout,
unambiguous language directions, pinned source revision, file integrity evidence,
and supported commercial weight-license evidence. Unknown or conflicting evidence
must remain excluded with a reason. Preserve attribution and upstream provenance.

Discovery retrieves metadata, not model weights. Only manual model installation
or a user-started translation/dubbing operation may download weights. Automatic
translation downloads are enabled by default; offline mode prohibits refresh and
downloads. Installed models remain usable subject to existing runtime gates.

Auto prefers approved direct OPUS routes. An explicit model selection remains
authoritative. Discovery, approval, downloading, integrity verification, runtime
initialization, and successful translation are separate states. Refresh must not
change the model revision or file contract of a translation already in progress.

Validation must include deterministic HTTP fixtures and opt-in real-model
translation. Fixture tests do not prove that an ONNX export runs, that a language
group prefix works, or that a full dub has a compatible TTS route.
