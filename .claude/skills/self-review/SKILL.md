---
name: self-review
description: Review changes against Nethermind conventions before creating a PR.
disable-model-invocation: true
allowed-tools: Bash(dotnet *), Bash(git *), Bash(gh *), Read, Grep, Glob
---

# Self-review

Review the current branch's changes against Nethermind conventions. Follow the **plan** below and report at each **checkpoint** before proceeding. Final output must use the **structured report** format.

---

## Plan

1. **Resolve diff scope** — Determine the exact diff to review (branch vs base or PR).
2. **Checkpoint: scope** — Report: base ref, file count, list of changed paths (by category: C#, csproj, YAML, other).
3. **Run automated checks** — Format verification; optionally build (if user wants).
4. **Checkpoint: automation** — Report: format pass/fail; build pass/fail (if run).
5. **Exact checks** — Deterministic, near-zero false positive rate checks.
6. **Rule-based review** — Apply convention checks against `.claude/rules/` files.
7. **Checkpoint: findings** — List all findings with severity, rule/source, file:line, and one-line description.
8. **Structured report** — Emit the final report (see template below).

---

## 1. Resolve diff scope

**Default (current branch vs master):**
```bash
BASE=$(git merge-base HEAD origin/master)
git diff $BASE...HEAD --stat
```
Use `git diff $BASE...HEAD` (not plain `git diff`) for all checks.

**If `$ARGUMENTS` is a PR number**, fetch that PR's diff instead:
```bash
gh pr diff $ARGUMENTS -- '*.cs' '*.csproj' '*.yml' '*.yaml'
```

---

## 2. Checkpoint: scope

- **Base:** `origin/master` (or PR base branch)
- **Files changed:** N
- **By category:** C# (n), .csproj (n), .yml/.yaml (n), other (n)
- **New .cs files:** (list, for SPDX check)
- **Paths:** (list or grouped by project/folder)

---

## 3. Run automated checks

- **Format:**
  `dotnet format whitespace src/Nethermind/ --folder --verify-no-changes`
- **Build (optional):**
  `dotnet build src/Nethermind/Nethermind.slnx -c Release`
  Only if the user or context asks for a build check.

---

## 4. Checkpoint: automation

- **Format:** pass | fail (if fail, list files that need formatting).
- **Build:** skipped | pass | fail (if run).

---

## 5. Exact checks

Deterministic checks with near-zero false positive rate.

### 5a. SPDX license header
```bash
git diff $BASE...HEAD --diff-filter=A --name-only -- '*.cs'
```
Each new `.cs` file must start with:
```
// SPDX-FileCopyrightText: <year> Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
```
Flag as **critical** if missing.

### 5b. PR template completeness
If reviewing a PR, check the body for unfilled placeholders:
- `_List the changes_`
- `_Optional. Remove if not applicable._`
- `Fixes #` with no issue number

Flag as **critical** if any placeholder is present.

### 5c. Forbidden directories
```bash
git diff $BASE...HEAD --name-only | grep -E '^src/(bench_precompiles|tests)/'
```
Flag as **critical** if any match.

---

## 6. Rule-based review

Apply these checks to the diff. Skip CI-covered concerns (formatting, build errors, test failures, CodeQL, dependency vulnerabilities).

| Check | What to look for | Rule reference |
|-------|------------------|----------------|
| **DI** | Manual wiring: `new BlockProcessor(`, `new TransactionProcessor(`, `new BranchProcessor(`, or long `new Foo(new Bar(...))` chains. | di-patterns.md |
| **Style** | `var` (except long generics), LINQ in hot paths, `== null` / `!= null` instead of `is null` / `is not null`. | coding-style.md |
| **CPM** | `Version=` in `<PackageReference>` — versions belong in `Directory.Packages.props`. | package-management.md |
| **Concurrency** | Fields written via `Interlocked.*` but read with plain access; missing `Volatile.Read`. | concurrency.md |
| **Tests** | Bug fixes without regression test. Tests mocking `IBlockTree`, `IWorldState`, etc. instead of using `TestBlockchain`. | test-infrastructure.md |

### Conditional domain checks

| Check | Condition | What to look for | Rule reference |
|-------|-----------|------------------|----------------|
| **Serialization** | Diff touches `Nethermind.Serialization.*/**` | `long`->`ulong` without `unchecked`, manual byte-shift instead of `BinaryPrimitives`, allocating encode where span-based exists. | serialization.md |
| **EVM** | Diff touches `Nethermind.Evm*/**` | LINQ in handlers, boxing of gas policy, exceptions in hot path, missing inlining. | evm/evm-conventions.md |
| **.github** | Diff touches `.github/**` | Concurrency, triggers, secrets in logs, matrix names out of sync. | github-workflows.md |

For each finding: note **severity** (critical = must fix; suggestion = consider fixing), **rule/source**, **file:line**, and **one-line description**.

---

## 7. Checkpoint: findings

List every finding, grouped by layer:

- **Exact (critical):** SPDX / template / forbidden-dirs findings
- **Rule-based — Critical:** (list)
- **Rule-based — Suggestions:** (list)

---

## 8. Structured report (final output)

```markdown
## Review report

**Scope:** <base ref> -> HEAD (or PR #N)
**Files:** <count> (C#: n, csproj: n, yml: n, other: n)

### Automation
| Check   | Result |
|---------|--------|
| Format  | pass / fail |
| Build   | skipped / pass / fail |

### Exact checks
| Check            | Result |
|------------------|--------|
| SPDX headers     | pass / N missing |
| PR template      | pass / N placeholders |
| Forbidden dirs   | pass / N violations |

### Findings

#### Critical (must fix)
- `path/to/file.cs:42` — Short description. [rule: di-patterns]

#### Suggestions (consider fixing)
- `path/to/file.cs:100` — Short description. [rule: coding-style]

### Summary
- Exact-check critical: N
- Rule-based critical: N
- Suggestions: N
```

If there are no findings, say so explicitly in the Summary.
