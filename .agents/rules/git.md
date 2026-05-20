# Git

Rules to follow when performing tasks around git version control, creating branches, merging, rebasing, etc.

## Rules

- Be wary of force pushing to branches, always confirm with the user beforehand.
- When performing a merge, ensure that no features are silently removed. If you are unsure which code to keep when resolving a merge conflict consult the user in an interactive manner.
- When creating a new branch, follow the convention of starting the branch name with: `perf/`, `feature/`, `test/`, `fix/` or `refactor/`.