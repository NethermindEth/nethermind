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
2. **`CONTRIBUTING.md`** and **`.editorconfig`**

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

4. **Impact assessment (mandatory for every candidate before recording).** Before assigning severity, answer:
   - **What accumulates or degrades?** Name the concrete resource: bytes, kernel handles, native allocations, file descriptors, pool slots, registrations. Read what the cleanup method actually releases — if it releases nothing for this specific instance, state that.
   - **How much, how fast?** Quantify: bytes per occurrence, occurrences per hour/day. If the answer is "once at shutdown" or "zero bytes," severity cannot be above COSMETIC.
   - **Is this path actually hit?** Check input domain bounds (e.g., size thresholds that gate allocation paths), config defaults, whether the code is reachable from any caller.
   - **Does the runtime already handle this?** Check DI container disposal, GC finalization, eviction/TTL mechanisms, bounded caches. If another mechanism already cleans this up, state which one and classify accordingly.

5. **Record findings** — file, line(s), one-sentence description, category, severity, frequency, impact assessment answers, ownership note, error-path note. Do NOT trace call graphs or search GitHub yet. Move fast.

6. **Mandatory sibling expansion.** After each confirmed finding: identify the structural pattern, search for every instance. Run `git log --oneline -10 -- <file>` to check for sibling fixes.

7. **After exhausting a category**, reflect: "What patterns suggest similar leaks I haven't searched for?"

8. **Stop ONLY when** all categories are covered AND reflection produces no new actionable patterns.

### Phase 1 Convergence Checkpoint

After all categories return:
- What patterns repeat across findings?
- Do findings in one subsystem imply identical bugs in siblings?
- Are there interfaces appearing as root cause?

**The convergence step is where the best findings come from — a leak in class A often implies the same leak in B, C, D.**

### Phase 2: Validation

**CRITICAL/HIGH findings** — full deep validation is mandatory.

For each CRITICAL/HIGH candidate:

**A. Triggerability proof:** Trace complete call graph to entry points. For race conditions: prove two threads can reach the race point — name the threads, show how launched, identify exact interleaving. If caller is serial, downgrade. For error-path leaks: identify what exception triggers it. For resource exhaustion: calculate accumulation rate.

**B. Adversary analysis:** Can an external attacker trigger this remotely? What control needed? Amplification? Mark **ADVERSARY-TRIGGERABLE** if triggerable without node operator access.

**C. Protocol context:** What Ethereum/L2 protocol concept? Why does the code exist? Real-world impact — not "memory pressure" but specifically what happens.

**D. Existing work check:** `gh search issues --repo <owner/repo> "<keyword>"` and `gh search prs --repo <owner/repo> "<keyword>"`. Check `git log --oneline -10 -- <file>`.

**E. Triggerability classification:** **TESTABLE** or **CONFIDENT-LEAK**. Add **CORRUPTION-RISK** / **ADVERSARY-TRIGGERABLE** as applicable.

**F. Test strategy (TESTABLE only):** Failing test -> passing test after fix. What injection needed? What assertion proves the leak?

**MEDIUM/LOW findings** — present Phase 1 findings with their impact assessments to the user and ask:

> *"Phase 1 produced N MEDIUM and M LOW findings with impact assessments but without deep validation (call graph tracing, existing-work checks, triggerability proofs). Some may be false positives. Would you like me to run deep validation on MEDIUM/LOW findings to filter those out? This will take additional time."*

If the user declines, output MEDIUM/LOW findings clearly marked: **"Phase 1 only — impact assessed but not deeply validated."**

---

## Self-Critique (Every Finding)

Before recording any finding at any severity, argue against yourself:

1. **"Does anything actually accumulate or degrade?"** — Read what the cleanup method releases for this specific instance. If the answer is "nothing" or "only a managed object GC already handles," it's COSMETIC.
2. **"Is the caller actually concurrent?"** — Prove it, don't assume from type signature.
3. **"Is the trigger actually reachable?"** — Check input domain bounds, size thresholds, config defaults. Dead code is not a leak.
4. **"Does the leak actually accumulate?"** — Is there eviction, TTL, GC, DI container disposal, or a finite bound?
5. **"Am I confusing poor style with a real bug?"** — If the quantified runtime impact is zero, classify as COSMETIC. State what best practice it violates and why the impact is zero. The user decides whether to fix cosmetic issues.

## Final Review Pass

1. **Severity calibration** — consistent across all severities, not just CRITICAL/HIGH
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
- **Severity**: CRITICAL | HIGH | MEDIUM | LOW | COSMETIC
- **Frequency**: [per-peer, per-block, per-request, per-shutdown, once, etc.]
- **Impact**: [quantified — bytes/handles per occurrence, accumulation rate, or "zero — [reason]"]
- **Ownership analysis**: [Who creates, who should clean up, why missing]
- **Error-path analysis**: [If applicable]
- **Fix complexity**: [SIMPLE (1-5 lines) | MEDIUM (10-30 lines) | HARD (refactor)]
```

COSMETIC means: technically violates a best practice but has zero quantified runtime impact. Include what practice it violates and why the impact is zero.

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
6. **COSMETIC findings** — separate section, with one sentence each explaining what practice is violated and why runtime impact is zero

## Rules

- **Non-test code only** — skip `*.Test*`, `*.Benchmark*`
- **Read actual code** — don't report based on grep matches alone
- **Prove triggerability** — "could theoretically race" is not enough
- **Config-gated is NOT dead** — report with config dependency noted
- **Check GitHub before reporting** — prevent duplicate work
- **Ownership transfers are not leaks** — verify the receiver cleans up
- **GC finalization is not proper disposal** — still report, but quantify the actual impact (finalizer queue pressure vs. zero impact)
- **`using` declarations (C# 8+)** are valid disposal
- **Process-lifetime singletons**: Check DI registration. If both the object and its event publishers are singletons with matching lifetimes, event unsubscription and disposal have zero runtime effect — classify as COSMETIC and state why.
- **Cancel() is NOT Dispose()** — but impact depends on what the CTS holds internally. After finding a CTS that is cancelled but not disposed, check: was it created with a timeout constructor? Was `CancelAfter()` called? Was its token passed to `Task.Delay()` or `CreateLinkedTokenSource()`? These create internal Timer handles or parent-token registrations that only `Dispose()` releases. A plain `new CancellationTokenSource()` that was cancelled with none of the above holds no unmanaged resources — classify as COSMETIC and state why.
- **Interfaces severing disposal chains** — before reporting, check if the DI container manages the concrete type's disposal. If the container tracks it, the interface gap has no runtime effect — classify as COSMETIC.
- **Override methods discarding parameters** — derived class ignoring disposable = leak
- **Double-dispose is worse than no-dispose** — corrupts shared state. But verify Dispose() is actually called from multiple threads before reporting a race — check all callers.
- **Empty Dispose() is a red flag** — check constructor for resources
- Codebase uses .NET 10, C# 14, Autofac for DI
