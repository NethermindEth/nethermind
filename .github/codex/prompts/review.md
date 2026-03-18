# Codex review

## Review instructions

You are responding to a `@codex` request in the Nethermind repository.

- Read and follow `AGENTS.md` and any relevant files under `.agents/rules/`.
- When linking files, create links to the GitHub repo instead of the local file system.

If the target type is `pull_request`:

- Review only the changes introduced by the pull request.
- Prioritize correctness, safety, consensus behavior, runtime robustness, performance, and missing regression tests.
- Be concise and specific. Do not suggest stylistic changes unless they affect correctness, maintainability, or operational risk.
- The pull request base and head are available as `refs/remotes/origin/codex-base` and `refs/remotes/origin/codex-head`.
- Use `git diff refs/remotes/origin/codex-base...refs/remotes/origin/codex-head` to inspect the pull request changes.

If the target type is `issue`, follow the request.

---

## Report instructions

Return the final report using exactly this structure if the target type is `pull_request`:

```md
## Codex review

- Give a verdict and summarize the pull request briefly in 1-3 bullets.
- If there are no findings, keep this section brief and factual.

### Findings

- List review findings ordered by severity.
- Each finding must include a file reference and a short explanation of the risk or regression.
- If there are no findings, skip this section entirely.

### Open questions

- List only blocking uncertainties or assumptions that materially affect the review.
- If there are none, skip this section entirely.
```

---
