# Linear workflow (agents + humans)

Linear workspace: [mesalimllc](https://linear.app/mesalimllc). Team key: **TS**.

Linear is the **source of truth for trackable work** across Trackdub repos (`Trackdub`, `Trackdub-gated`, `api.trackdub`, `portal.trackdub`, `trackdub.com`, and legacy archive). Agents must **reference and update Linear autonomously** as progress is made or new work is discovered. Do not wait for a human to file the issue first.

Canonical agent-facing copy also lives in Linear document **Agent Linear Workflow** (team Trackdub).

## Projects

| Project | Purpose |
|---|---|
| Desktop App | Gated Avalonia product (`Trackdub-gated`) |
| Public Core | Apache-2.0 engine/SDK/CLI (`Trackdub`) |
| Cloud & Portal | API + portal |
| Marketing Site | `trackdub.com` + brand |
| Platform & Tooling | CI, integrations, release, agent ops |
| Near-term Product Backlog | Short-horizon P0–P5 product items |
| Legacy Milestone Archive | Imported open issues from `Trackdub-Monorepo-Archive` |

## Agent loop (mandatory)

1. **Before coding:** search Linear for an existing issue (`TS-*`, backlog id like `P0-4`, or matching title). Prefer update over duplicate create.
2. **If none exists:** create an issue on the correct project with labels (`repo:*`, `area:*`, `agent-owned`). Attach GitHub/Figma/Notion links.
3. **While working:** set status to **In Progress**. Comment blockers, decisions, and proof links.
4. **When finished:** set **Done** only with honest evidence (tests, logs, screenshots). Never fake readiness.
5. **New work found mid-task:** create Linear issues immediately. Use `needs-triage` when a product call is required.
6. **Pull requests:** include `TS-xxx` or `Fixes TS-xxx` in the PR body once the GitHub integration is connected.

## Labels

- `repo:core` | `repo:gated` | `repo:api` | `repo:portal` | `repo:web` | `repo:archive`
- `area:ui` | `area:pipeline` | `area:inference` | `area:ci` | `area:docs` | `area:integrations`
- `agent-owned` (agents may create/update freely)
- `needs-triage` (human product decision)
- Built-ins: `Bug`, `Improvement`, `Feature`

## Integrations

GitHub, Notion, and Figma OAuth are connected (TS-5 / TS-6 / TS-19 Done).

| Status | Integration | Notes |
|---|---|---|
| Done | GitHub | Mention `TS-xxx` / `Fixes TS-xxx` in PRs |
| Done | Notion embeds | Paste Linear URLs into Notion for previews |
| Done | Figma embeds + plugin | Paste Figma frames into Linear; use Linear plugin in Figma |
| Removed | Linear Release → Actions | Job deleted from `.github/workflows/ci.yml`; it was failing on push to `main` and the integration is no longer wired up |

Pipeline: [Trackdub Release](https://linear.app/mesalimllc/pipeline/trackdub-release/releases) (scheduled). Releases are cut manually; there is no longer a Linear Release Sync workflow to run.

Canonical design links (also in `docs/reference/design-standards.md`):

- Figma Design System: https://www.figma.com/design/vUAGF65aDO1a83u5COYWBG/
- Notion Specs: https://www.notion.so/Trackdub-Specs-d76b5426aae5450cb29404212a7ffe79

## MCP / API

Cursor agents use the Linear MCP (`plugin-linear-linear`): `list_issues`, `save_issue`, `save_comment`, `save_project`, `list_projects`, etc.

Prefer commenting progress on the issue over burying status only in chat.

## Conflict order

Source code/tests > task instructions > `AGENT_CONTEXT.md` (gated) > `AGENTS.md` > Linear issue text > other docs.

If Linear and the code disagree on status, update Linear to match verified code state and note the reconciliation in a comment.
