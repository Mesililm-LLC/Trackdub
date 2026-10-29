# Trackdub Documentation

## Categories

- [decisions/](decisions/) — Architecture Decision Records (ADRs)
- [architecture/](architecture/) — System architecture and design
- [specs/](specs/) — Technical specifications and requirements
- [audits/](audits/) — Completed investigations and reports
- [operations/](operations/) — Operational procedures
- [development/](development/) — Developer procedures and guides
- [reference/](reference/) — Technical reference material
- [legal/](legal/) — Legal and attribution material
- [strategy/](strategy/) — Roadmap and strategic direction
- [plans/](plans/) — Active cross-cutting implementation plans

## Governance

See [repository-policy.md](repository-policy.md) for repository organization,
contribution, and governance details.

# Repository Policy

## Organization

This repository (`Mesililm-LLC/Trackdub`) contains the public core of the
Trackdub dubbing engine: SDK, CLI, pipeline, inference, media processing,
infrastructure, and neutral licensing mechanisms.

Licensed under Apache-2.0 (see [LICENSE](../LICENSE) and [NOTICE](../NOTICE)).

## Repository Structure

```
src/          — Source projects
tests/        — Test projects
docs/         — Documentation (this tree)
tools/        — Development tooling
scripts/      — Build and CI scripts
runtime/      — Runtime manifests
resources/    — Model optimization recipes
assets/       — Demo media
.github/      — CI workflows and templates
```

## Contribution Guidelines

- Follow the dependency direction rules in [AGENTS.md](../AGENTS.md).
- Run `dotnet build Trackdub.slnx -m:1` and `dotnet test Trackdub.slnx -m:1`
  before submitting changes.
- Use imperative commit titles: `Add ...`, `Fix ...`, `Remove ...`
- Changes that affect model governance must update the bundled manifest.
- Cross-platform compatibility is a requirement for all changes.

## Documentation Taxonomy

| Directory | Contains |
|-----------|----------|
| `decisions/` | Architecture Decision Records (ADR-NNNN) |
| `architecture/` | System design, dependency graphs, audits |
| `specs/` | Stable technical specifications |
| `audits/` | Completed investigation reports |
| `operations/` | Deployment, CI, runtime procedures |
| `development/` | Developer guides and troubleshooting |
| `reference/` | Technical lookup material |
| `legal/` | Licenses, attribution, model policies |
| `strategy/` | Roadmap and long-term direction |
| `plans/` | Active cross-cutting implementation plans |

## Model Governance

Only ONNX models with verified commercial licenses are used. The bundled model
manifest is the single source of truth. Unknown or non-commercial licenses are
treated as unsafe and blocked.

## Native Dependencies

Downloaded native binaries (DLLs, dylibs, SOs, FFmpeg, libmpv, EP bundles) are
not tracked in this repository. Manifests, URLs, hashes, and acquisition scripts
are tracked instead.

## Agent Instructions

[AGENTS.md](../AGENTS.md) is the canonical instruction file for automated
contributors. It defines the dependency graph, build commands, coding style,
testing requirements, and model governance rules.
