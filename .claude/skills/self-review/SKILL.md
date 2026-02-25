---
name: self-review
description: Pre-PR convention check for Nethermind. Runs format verification, SPDX header checks, and convention checks against .claude/rules/. Use when asked to "self-review", "check before PR", "run pre-PR checks", or "check conventions".
allowed-tools: Bash(dotnet format*), Bash(git diff*), Bash(git merge-base*), Bash(git log*), Bash(gh pr diff*), Read, Grep, Glob
---

# Self-review

Check the current branch's changes against Nethermind conventions before creating a PR. Follow the plan below, reporting at each checkpoint before proceeding.

---

## Plan

1. **Resolve diff scope** — determine the diff to review
2. **Checkpoint: scope** — report file count and categories
3. **Automated checks** — format, SPDX headers, forbidden directories
4. **Checkpoint: automation** — report pass/fail
5. **Convention checks** — apply rules from `.claude/rules/`
6. **Checkpoint: findings** — list findings with severity
7. **Structured report** — emit final report

---

## Step 1: Resolve diff scope

Default (current branch vs master):
```bash
BASE=$(git merge-base HEAD origin/master)
git diff $BASE...HEAD --stat
```

If `$ARGUMENTS` is a PR number:
```bash
gh pr diff $ARGUMENTS -- '*.cs' '*.csproj' '*.yml' '*.yaml'
```

Always use three-dot diff (`$BASE...HEAD`), not plain `git diff`.

---

## Step 2: Checkpoint — scope

Report: base ref, file count, breakdown by category (C#, csproj, yml, other), list of new `.cs` files.

---

## Step 3: Automated and exact checks

### Format
```bash
dotnet format whitespace src/Nethermind/ --folder --verify-no-changes
```

### SPDX license header
```bash
git diff $BASE...HEAD --diff-filter=A --name-only -- '*.cs'
```
Each new `.cs` file must start with:
```
// SPDX-FileCopyrightText: <year> Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
```
Flag as **critical** if missing.

### PR template completeness
If reviewing a PR, check the body for unfilled placeholders:
- `_List the changes_`
- `_Optional. Remove if not applicable._`
- `Fixes #` with no issue number

### Forbidden directories
```bash
git diff $BASE...HEAD --name-only | grep -E '^src/(bench_precompiles|tests)/'
```
Flag as **critical** if any match.

---

## Step 4: Checkpoint — automation

Report: format pass/fail, SPDX pass/fail, forbidden-dirs pass/fail.

---

## Step 5: Convention checks

Apply these to the diff. Each maps to a `.claude/rules/` file. Skip anything CI already covers (formatting, build errors, test failures, CodeQL, dependency vulnerabilities).

### Always apply

| Check | What to look for | Rule |
|-------|------------------|------|
| **DI** | Manual wiring: `new BlockProcessor(`, `new TransactionProcessor(`, `new BranchProcessor(`, or long `new Foo(new Bar(...))` chains. Check `Nethermind.Init/Modules/` first. | `di-patterns.md` |
| **Style** | `var` (except long generics), LINQ in hot paths, `== null` / `!= null` instead of `is null` / `is not null`. | `coding-style.md` |
| **CPM** | `Version=` in `<PackageReference>` — versions belong in `Directory.Packages.props`. | `package-management.md` |
| **Concurrency** | Fields written via `Interlocked.*` but read with plain access; missing `Volatile.Read`. | `concurrency.md` |
| **Tests** | Bug fixes without regression test. Tests mocking `IBlockTree`, `IWorldState`, etc. instead of using `TestBlockchain`. | `test-infrastructure.md` |
| **Robustness** | `async void`, `.Result`/`.Wait()`, missing `await`, missing `CancellationToken`, `IDisposable` not in `using`, empty `catch`. | `robustness.md` |

### Conditional (apply when diff touches matching paths)

| Check | Condition | What to look for | Rule |
|-------|-----------|------------------|------|
| **Serialization** | `Nethermind.Serialization.*/**` | `long`->`ulong` without `unchecked`, manual byte-shift instead of `BinaryPrimitives`, allocating encode where span-based exists. | `serialization.md` |
| **EVM** | `Nethermind.Evm*/**` | LINQ in handlers, boxing of gas policy, exceptions in hot path, missing inlining. | `evm/evm-conventions.md` |
| **GitHub** | `.github/**` | Concurrency, triggers, secrets in logs, matrix names out of sync. | `github-workflows.md` |

For each finding: note **severity** (critical = must fix; suggestion = consider fixing), **rule/source**, **file:line**, and **one-line description**.

---

## Step 6: Checkpoint — findings

List every finding grouped by:
- **Exact (critical):** SPDX / template / forbidden-dirs violations
- **Rule-based — Critical:** must fix
- **Rule-based — Suggestions:** consider fixing

---

## Step 7: Structured report

```markdown
## Self-review report

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
