---
name: resource-leak-audit
description: Audit C#/.NET code for resource leaks, IDisposable misuse, CTS lifecycle bugs, unbounded growth, handle exhaustion, and dispose race conditions. Use when asked to "audit for leaks", "check for resource leaks", "find dispose issues", "review for memory leaks", or when reviewing a PR that touches resource management, disposal, CancellationTokenSource, ArrayPool, event subscriptions, or async coordination patterns.
---

# Resource Leak Audit

## Overview

Systematic audit for resource leaks in C#/.NET code. Covers IDisposable/IAsyncDisposable misuse, unbounded collection growth, pinned memory, unmanaged allocations, event handler GC roots, abandoned tasks, closure captures, static accumulation, finalizer queue pressure, thread-local storage, TCS lifecycle, dispose races, and constructor exception leaks.

## Mode Selection

Determine the audit scope before starting:

### PR Mode (default when reviewing a PR)
Audit only files changed in the PR. Use `git diff` to get the changed files, then apply the full methodology to those files plus their immediate callers/callees.

Steps:
1. Get changed files: `git diff origin/master...HEAD --name-only` (three-dot diff uses the merge-base, so it works correctly both locally and in CI even if master has advanced)
2. Filter to non-test C# files (exclude `*.Test*`, `*.Benchmark*`)
3. For each changed file, also read classes it inherits from and interfaces it implements
4. Apply Phase 1 search only for categories relevant to the changed code
5. Apply Phase 2 validation for any CRITICAL/HIGH findings
6. Skip Step 0a (git history mining) unless a finding needs prior-fix context

### Full Audit Mode (when asked to audit the codebase)
Audit all non-test code in `src/Nethermind/`. This is the exhaustive mode — use all steps below including Step 0.

---

## Before Starting

Read these — they define conventions and inform what counts as a leak:

1. **All rule files** in `.agents/rules/` — always list the directory rather than relying on a fixed list
2. **All reference files** in `.agents/references/` for relevant subsystems
3. **`CONTRIBUTING.md`** and **`.editorconfig`**

## Methodology — Two-Phase Audit with Reviewer Gate

### Step 0: Study Prior Fixes and Codebase Patterns (Full Audit Mode)

**0a. Mine prior leak-fix commits:**

```
git log --oneline --grep="dispose" --grep="leak" --all-match -30
git log --oneline --grep="memory leak" -20
git log --oneline --grep="IDisposable" -20
git log --oneline --grep="ArrayPool" -20
git log --oneline --grep="buffer leak" -10
git log --oneline --grep="double dispose" -10
git log --oneline --grep="race condition" --grep="dispose" --all-match -10
```

For the top 20 most relevant commits, read the diff (`git show <hash>`). For each: What was the leak pattern? What was the fix? Are there similar patterns elsewhere still unfixed? Do sibling classes have the same bug?

**0b. Discover the codebase's resource management conventions:**

Before searching for leaks, learn what safe patterns exist. Leaks often come from NOT using an available safe wrapper. For each resource type, answer:

1. **Pool buffer management:** Disposable wrappers around `ArrayPool.Rent`/`Return`? Search: struct/class types in `**/Buffers/*`, `**/Collections/*` implementing IDisposable referencing ArrayPool.
2. **Thread-safe disposal:** What pattern for `_disposed` guards? Search: `Interlocked` near `_disposed`. Then find `bool _disposed` fields not following it.
3. **CTS lifecycle:** Helper combining `Cancel()` + `Dispose()` atomically? Search: methods taking `ref CancellationTokenSource` calling both.
4. **Buffer ownership:** Marker interfaces for owned resources (`IOwned*`, `IMemoryOwner`)? Disposal extension methods?
5. **Test infrastructure for leak detection:** Tracking/counting pool wrappers in test projects?

Record findings — used in Phase 1 backward searches.

### Parallelization

If subagents are available:

- **Step 0:** Run 0a and 0b in parallel
- **Phase 1:** One subagent PER category. Each exhausts ONE category completely
- **Convergence checkpoint:** After first wave, review findings for cross-cutting patterns, launch second wave
- **Phase 2:** Validation subagents for CRITICAL/HIGH candidates
- **Final review:** Separate reviewer subagent with complete findings for fresh perspective

---

### Phase 1: Exhaustive Search (Breadth-First)

**The most common failure mode is spending all time validating a few findings while missing dozens of others. Phase 1 separates search from validation.**

For each category (see @references/pattern-categories.md):

1. **Search exhaustively** using three complementary strategies:
   - **Forward search (construction -> disposal):** Grep for resource construction. For each hit, follow forward to verify cleanup exists.
   - **Backward search (safe pattern bypass):** Using Step 0b wrappers, search for code using the raw pattern instead of the available safe alternative. **Every safe pattern implies a class of bugs from not using it.**
   - **Disposal-site audit (Dispose -> completeness):** For each IDisposable class in the subsystem, read its Dispose method and compare against constructor and field declarations.
   - Search for constructors, factory methods, and assignments — not just type names.

2. **Check EVERY match — not a sample.** Report "N total matches, M confirmed findings, (N-M) verified clean". If 100+, split across subagents.

3. **For every candidate**, read surrounding code (method, class's Dispose, callers if disposable is returned):
   - Is there a `using` statement/declaration?
   - Is there a `try/finally` with cleanup?
   - If it's a field, does the owning class clean it up on shutdown/disposal?
   - If ownership transfers, does the receiver clean it up?
   - On error/exception paths, is the resource still cleaned up?

4. **Record findings quickly** — file, line(s), one-sentence description, category, severity, frequency, ownership note, error-path note. Do NOT trace call graphs or search GitHub yet. Move fast.

5. **Mandatory sibling expansion.** After each confirmed finding: identify the structural pattern, search for every instance. Run `git log --oneline -10 -- <file>` to check for sibling fixes.

6. **After exhausting a category**, reflect: "What patterns suggest similar leaks I haven't searched for?"

7. **Stop ONLY when** all categories are covered AND reflection produces no new actionable patterns.

### Phase 1 Convergence Checkpoint

After all categories return:
- What patterns repeat across findings?
- Do findings in one subsystem imply identical bugs in siblings?
- Are there interfaces appearing as root cause?

**The convergence step is where the best findings come from — a leak in class A often implies the same leak in B, C, D.**

### Phase 2: Deep Validation (CRITICAL/HIGH Only)

For each CRITICAL/HIGH candidate:

**A. Triggerability proof:** Trace complete call graph to entry points. For race conditions: prove two threads can reach the race point — name the threads, show how launched, identify exact interleaving. If caller is serial, downgrade. For error-path leaks: identify what exception triggers it. For resource exhaustion: calculate accumulation rate.

**B. Adversary analysis:** Can an external attacker trigger this remotely? What control needed? Amplification? Mark **ADVERSARY-TRIGGERABLE** if triggerable without node operator access.

**C. Protocol context:** What Ethereum/L2 protocol concept? Why does the code exist? Real-world impact — not "memory pressure" but specifically what happens.

**D. Existing work check:** `gh search issues --repo <owner/repo> "<keyword>"` and `gh search prs --repo <owner/repo> "<keyword>"`. Check `git log --oneline -10 -- <file>`.

**E. Triggerability classification:** **TESTABLE** or **CONFIDENT-LEAK**. Add **CORRUPTION-RISK** / **ADVERSARY-TRIGGERABLE** as applicable.

**F. Test strategy (TESTABLE only):** Failing test -> passing test after fix. What injection needed? What assertion proves the leak?

---

## Self-Critique (After Each Category)

For each CRITICAL/HIGH finding, argue against yourself:

1. **"Is the caller actually concurrent?"** — Prove it, don't assume from type signature.
2. **"Is the trigger actually reachable?"** — Is the config default on? Are there tests for this path?
3. **"Does the leak actually accumulate?"** — Is there eviction, TTL, or GC?
4. **"Am I confusing poor style with a real bug?"** — Singleton at shutdown = low priority. Per-peer for days = real.

## Final Review Pass

1. **Severity calibration** — consistent across all CRITICAL/HIGH
2. **Duplicate detection** — merge same root cause from different angles
3. **False positive sweep** — if trigger can't be stated in one concrete sentence, downgrade
4. **Missing context** — every CRITICAL/HIGH needs all mandatory fields

## Output Format

### Phase 1 (all findings)

```
### Finding [N]: [Short title]
- **File**: `Nethermind.Project/Path/File.cs`
- **Line(s)**: [line numbers]
- **What leaks**: [precise description]
- **Category**: [from pattern list]
- **Severity**: CRITICAL | HIGH | MEDIUM | LOW
- **Frequency**: [per-peer, per-block, per-request, per-shutdown, once, etc.]
- **Ownership analysis**: [Who creates, who should clean up, why missing]
- **Error-path analysis**: [If applicable]
- **Fix complexity**: [SIMPLE (1-5 lines) | MEDIUM (10-30 lines) | HARD (refactor)]
```

### Phase 2 additions (CRITICAL/HIGH — append)

```
- **Protocol context**: [What protocol concept, why code exists]
- **Exact trigger**: [Specific event causing the leak]
- **Call graph**: [Entry point -> ... -> buggy method]
- **Adversary analysis**: [Can attacker trigger? Control needed? Amplification?]
- **Accumulation rate**: [Leaks/hour, bytes/object, when degradation occurs]
- **Real-world impact**: [Specific degradation on running node]
- **Trigger**: TESTABLE | CONFIDENT-LEAK [| CORRUPTION-RISK] [| ADVERSARY-TRIGGERABLE]
- **Trigger explanation**: [How test triggers / Why not deterministic]
- **GitHub context**: [Existing issues/PRs]
- **Prior PR context**: [Similar fixes with commit hash]
- **Test complexity**: [SIMPLE | MEDIUM | HARD]
- **Test strategy**: [Failing test description]
```

### Final Output

1. Complete deduplicated findings list
2. **Triage summary** by reachability: Practically triggerable / Config-gated / Theoretically possible / Impossible in current architecture
3. **Test plan summary** — one-line per TESTABLE finding
4. **Potentially missed** — suspected patterns needing more investigation
5. **Confidence assessment** — coverage estimate per category

## Rules

- **Non-test code only** — skip `*.Test*`, `*.Benchmark*`
- **Read actual code** — don't report based on grep matches alone
- **Prove triggerability** — "could theoretically race" is not enough
- **Config-gated is NOT dead** — report with config dependency noted
- **Check GitHub before reporting** — prevent duplicate work
- **Ownership transfers are not leaks** — verify the receiver cleans up
- **GC finalization is not proper disposal** — still report
- **`using` declarations (C# 8+)** are valid disposal
- **Process-lifetime singletons are borderline** — low priority unless per-peer/block/request
- **Cancel() is NOT Dispose()** — most common pattern. Always verify Dispose follows Cancel
- **Interfaces severing disposal chains** — report the interface as root cause
- **Override methods discarding parameters** — derived class ignoring disposable = leak
- **Double-dispose is worse than no-dispose** — corrupts shared state
- **Empty Dispose() is a red flag** — check constructor for resources
- Codebase uses .NET 10, C# 14, Autofac for DI
