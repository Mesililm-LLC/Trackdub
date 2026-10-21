# Translation discovery policy: opus-mt-publishers-v1

The catalog discovers Marian OPUS-MT exports in the exact `onnx-community` and
`Xenova` Hugging Face namespaces. Namespace approval permits discovery and
provisioning; it does not establish installed files or runtime readiness.

An eligible export must have an immutable commit, the supported ONNX encoder
and merged decoder layout, tokenizer/configuration files, and complete SHA-256
metadata. Small non-LFS artifacts are fetched at the pinned commit and hashed.
ONNX weights are never fetched during discovery.

The accepted weight-license identifiers are `apache-2.0`, `mit`, and
`cc-by-4.0`. A linked Helsinki model may supply missing license metadata. Export
and upstream license conflicts exclude the export. The descriptor records the
pinned license evidence URL. Publisher membership alone does not set the
commercial-use verification flag in the effective manifest.

Dedicated pairs use explicit repository directions. Language groups require
explicit upstream source and target lists. Multi-target routes additionally
require the exact target token in the exported vocabulary. Missing evidence
excludes the model with a diagnostic instead of guessing translation directions.

Snapshots contain descriptors and exclusion reasons, never model weights.
Offline refresh returns the cached snapshot without network access. Failed
refreshes preserve the last complete snapshot; cancellation propagates to the
caller. The consumer schedules background refresh when the snapshot is older
than 24 hours. Model downloads and runtime verification remain separate gates.
