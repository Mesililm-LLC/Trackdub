# License History

Trackdub is split into two repositories with different licenses:

- **`mesalimllc/Trackdub`** (this repository) — the public core: SDK, CLI,
  pipeline, inference, media processing, infrastructure, and neutral
  licensing mechanisms. Licensed under [Apache License, Version 2.0](../../LICENSE)
  (see the root [NOTICE](../../NOTICE)).
- **The Trackdub desktop product** — a separate, private repository that
  depends on this public core. It owns the desktop application shell,
  product policy (export tiers, watermarking, entitlements), activation,
  packaging, and release signing. It is proprietary and is not covered by
  the Apache-2.0 license in this repository.

This repository was established with a fresh root history as the public
core. It carries only Apache-2.0-compatible source and does not contain
desktop product code, activation-server code, or proprietary packaging
logic. Detailed historical ownership evidence for code predating this split
is retained privately and is out of scope for this public document.
