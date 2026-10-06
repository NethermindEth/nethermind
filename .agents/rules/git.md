# Git

Rules to follow when performing tasks around git version control, creating branches, merging, rebasing, etc.

## Rules

- Be wary of force pushing to branches, always confirm with the user beforehand.
- When performing a merge, ensure that no features are silently removed. If you are unsure which code to keep when resolving a merge conflict consult the user in an interactive manner.
- When creating a new branch, follow the convention of starting the branch name with: `perf/`, `feature/`, `test/`, `fix/` or `refactor/`.
- Write commit messages and PR titles as [Conventional Commits](https://www.conventionalcommits.org/): `type(scope): description`. PRs are squash-merged with the PR title as the commit subject, so the title is what lands on `master`.
  - `type` is one of `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `build`, `ci` or `chore`; [pr-labeler.yml](../../.github/workflows/pr-labeler.yml) maps it to a PR label.
  - `scope` is optional: a short lowercase area such as `rpc`, `evm` or `zkevm`.
  - Start the description with a lowercase imperative verb, and do not end it with a period.
  - Mark a breaking change with `!` before the colon and always give it a scope, e.g. `feat(rpc)!: remove eth_foo`; the labeler does not recognize `feat!:`.
- When opening a PR non-interactively (`gh pr create --body ...`), GitHub's template auto-fill does not apply — populate the body from [.github/pull_request_template.md](../../.github/pull_request_template.md) yourself (Changes list, type-of-change checkboxes, Testing/Documentation sections). The checkboxes drive automatic PR labeling.