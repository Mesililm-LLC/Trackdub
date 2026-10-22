# CodeQL advanced setup (Trackdub)

Trackdub uses **advanced CodeQL only** via [`.github/workflows/codeql.yml`](../../.github/workflows/codeql.yml). Do not run GitHub **default** CodeQL in parallel on this repository.

## Why one setup

| | Default CodeQL (`CodeQL` workflow) | Advanced (`CodeQL Advanced`) |
|---|-----------------------------------|------------------------------|
| Source | Org/repo dynamic setup | `.github/workflows/codeql.yml` |
| C# build | `build-mode: none` on Linux | Manual `Trackdub.slnx` build on Windows |
| Frontend (JS/TS) | Default | Not in the advanced matrix today (it covers `actions`, `csharp`, `python` only) |
| Queries | Default suite | `security-extended,security-and-quality` |
| Paths | Whole repo | `.github/codeql/codeql-config.yml` scopes |

Running both wastes Actions minutes and produces weaker C# results (no compiled DB).

## Current state (check periodically)

```powershell
# Repo default-setup flag (want: state = not-configured)
gh api repos/mesalimllc/Trackdub/code-scanning/default-setup --jq '{state, query_suite}'

# Applied org security configuration (default setup should be disabled when advanced-only)
gh api repos/mesalimllc/Trackdub/code-security-configuration --jq '.configuration | {name, code_scanning_default_setup, code_scanning_options}'

# Recent Advanced runs
gh run list --repo mesalimllc/Trackdub --workflow=codeql.yml --limit 5
```

If you see **both** `CodeQL` and `CodeQL Advanced` on the same push, default setup is still enabled at org or repo level.

## One-time org fix (requires org admin)

The `mesalimllc` org applies enforced configuration **`mesalimllc-org-config-1`**, which currently enables **Code scanning default setup**. That spawns the dynamic `CodeQL` workflow even when the repo workflow file is advanced.

**UI (recommended):**

1. Open [mesalimllc org security configuration](https://github.com/organizations/mesalimllc/settings/security_products/configurations/edit/259214).
2. Under **Code scanning**, set **Default setup** to **Disabled**.
3. Keep **Allow advanced setup** enabled.
4. Save.

**API (needs `admin:org` on `gh auth`):**

```powershell
gh auth refresh -h github.com -s admin:org

@'
{
  "code_scanning_default_setup": "disabled",
  "code_scanning_options": {
    "allow_advanced": true
  }
}
'@ | gh api -X PATCH orgs/mesalimllc/code-security/configurations/259214 --input -
```

**Repo-level backup:**

1. Repository **Settings → Advanced Security → Code security**.
2. **CodeQL analysis** menu → **Switch to advanced** (or disable default CodeQL if offered).
3. Confirm `.github/workflows/codeql.yml` remains the active workflow.

Then confirm repo default setup:

```powershell
gh api repos/mesalimllc/Trackdub/code-scanning/default-setup -X PATCH -f state=not-configured
```

## Verify Advanced

```powershell
gh workflow run codeql.yml --repo mesalimllc/Trackdub
gh run list --repo mesalimllc/Trackdub --workflow=codeql.yml --limit 1
```

Expect three matrix jobs: `actions`, `csharp` (Windows), `python`. The workflow file is currently
disabled at the repository level (`gh api` returns `state: disabled_manually`), so re-enable it before
dispatching; until then, pre-merge analysis comes from GitHub's default code scanning setup.

## Related workflows

- **`code-coverage.yml`**: Coverlet + GitHub Code Quality upload (test coverage, not SAST).
- **`ci.yml`**: Build/test gate; does not replace CodeQL.

## Config files

- Workflow: `.github/workflows/codeql.yml`
- Query/path config: `.github/codeql/codeql-config.yml`
