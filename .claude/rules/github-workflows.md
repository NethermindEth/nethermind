---
paths:
  - ".github/**/*.yml"
  - ".github/**/*.yaml"
  - ".github/**/*.md"
  - ".github/**/*.sh"
  - ".github/**/*.py"
---

# .github — Workflows and automation

Conventions for GitHub Actions, CODEOWNERS, and repo automation under `.github/`.

## Workflows

- **Naming**: Use kebab-case; descriptive name (e.g. `nethermind-tests.yml`, `run-expb-reproducible-benchmarks.yml`).
- **Concurrency**: Prefer `concurrency: group: ${{ github.workflow }}-${{ github.ref }}` with `cancel-in-progress: true` for PR/push workflows to avoid duplicate runs.
- **Triggers**: Be explicit — `pull_request:`, `push: branches: [master]`, or `workflow_dispatch:` with inputs as needed.
- **Secrets**: Never log or echo secrets; use `${{ secrets.X }}` and restrict env vars to the job that needs them.
- **Matrix**: For test workflows, the project list in `nethermind-tests.yml` is the source of truth; keep matrix project names in sync with actual test project names (e.g. `Nethermind.Evm.Test`).
- **Runner labels**: Reproducible benchmarks use `reproducible-benchmarks`; other jobs typically use `ubuntu-latest` unless the workflow doc specifies otherwise.
- **Temporary files**: Workflows that render or generate config (e.g. benchmark config) must do so to a temp path and must not modify tracked source files.

## Actions (composite or custom)

- Custom actions live under `.github/actions/<name>/` with `action.yaml` and scripts (e.g. `runner-setup.sh.j2`, `runner-configure.sh`).
- Scripts must be executable and safe for the runner OS (Linux unless noted).
- Prefer `actions/checkout` and standard `actions/*` where possible; document any third-party action version and reason.

## Pull request template

- `.github/pull_request_template.md`: Fill in "Changes", tick type-of-change checkboxes, and complete Testing/Documentation sections. Checkboxes drive PR labeling; do not remove required sections.

## CODEOWNERS

- Update `.github/CODEOWNERS` when adding new critical paths or ownership changes; keep paths and teams in sync with repo structure.

## Notes for agents

- Do not change workflow logic (triggers, steps, matrices) without explicit user request.
- When adding a new workflow, follow existing patterns (concurrency, env, job names) and reference AGENTS.md for benchmark/reproducible-workflow specifics.
