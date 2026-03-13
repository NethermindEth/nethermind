---
name: eip-implementer
description: Use when the user says "implement EIP-XXXX", "add support for EIP-XXXX", "/eip-implementer XXXX", or asks to plan an EIP implementation in the Nethermind codebase.
allowed-tools: [Bash(git diff*), Bash(git merge-base*), Bash(git log*), Bash(git status*), Read, Grep, Glob, WebFetch]
---

# EIP Implementer

## Progress tracker

Copy and track as you go:

```
EIP-XXXX Implementation:
- [ ] Step 1: Fetch EIP spec — extract and present ALL fields listed below
- [ ] Step 2: Explore codebase (similar EIPs, determine target fork)
- [ ] Step 2a: Confirm target fork with user (WAIT — if EIP doesn't specify one, or fork doesn't exist, or user wants to skip fork)
- [ ] Step 3: Route spec changes to implementation patterns
- [ ] Step 4: Present implementation plan (WAIT for user approval)
- [ ] Step 5: Load relevant `.agents/rules/` per Codebase Rules, then implement
- [ ] Step 6: Build, test (all related EIPs), and format
- [ ] Step 7: Self-review via subagent with review skill
```

## Workflow

### Step 1 — Fetch and Summarize the EIP

Fetch the EIP spec using `WebFetch`:

```
Primary:  https://raw.githubusercontent.com/ethereum/EIPs/refs/heads/master/EIPS/eip-{number}.md
Fallback: https://eips.ethereum.org/EIPS/eip-{number}
```

The raw GitHub URL returns full markdown with formulas and pseudocode intact. The eips.ethereum.org fallback renders HTML that WebFetch may not fully capture. If both fail, ask the user for a direct link.

**You MUST present ALL of the following to the user before moving to Step 2.** If a section is empty in the EIP, state "None" explicitly.

- **EIP number + title**
- **Category** (Core, Networking, Interface, ERC/EIP, Meta, Informational)
- **Requires** — if the EIP header lists `requires`, **fetch those prerequisite EIPs too**. Specs are deltas — they assume the required EIPs are already active and show only what changes. Without reading the base EIP, simplified formulas and references won't make full sense.
- **Abstract** — one-paragraph summary
- **Key specification changes** — bullet list of what the spec mandates (new opcodes, new tx type, new precompile, new RPC methods, new consensus rules, etc.)
- **Backwards compatibility** notes
- **Security considerations** — **MUST** extract and list attack vectors, edge cases, and implementation pitfalls. Some EIPs have 5+ subsections here — each one can surface a validation check or edge case your implementation needs to handle.
- **Test cases** — **MUST** extract and list any test categories or test vectors the EIP provides (inline vectors, links to `ethereum/execution-spec-tests`, or links to `../assets/eip-XXXX/`). If present, fetch them and use them to validate your implementation in Step 5. If the EIP describes test categories without concrete vectors, use those categories to structure your test plan.
- **Reference implementation** — if present, use it to understand invariants and relationships between concepts, **not** as a template for code structure or organization

### Step 2 — Explore the Codebase

Before planning, ground yourself in the current state:

1. **Find a similar EIP** — search for a recently implemented EIP in the same category (e.g., if implementing a new opcode, look at how `MCOPY` or `TLOAD` was added). Read its spec flag, EVM handler, and tests to understand the real pattern. **Critically: run `git log --oneline --all --grep="IsEip{similar_number}" --name-only` to see the full list of files that similar EIP touched** — this reveals test infrastructure files (builders, base classes, bytecode extensions) that `implementation-patterns.md` may not list.
2. **Determine the target fork** — check whether the EIP spec names a target fork. If it does, verify the fork exists in `Nethermind.Specs/Forks/` and `MainnetSpecProvider.cs`. If the EIP **does not specify a fork** (common for Draft/Review status), list the available forks and **WAIT to ask the user** which fork to target — do not guess. The user can choose to:
   - Target an existing fork
   - Use the `fork-creator` skill to create a new one
   - Skip fork assignment entirely (the EIP flag will be dormant until a fork enables it)
3. **Check for partial work** — search for `IsEip{number}` to see if the EIP is already partially implemented.

### Step 3 — Route to Implementation Patterns

Read `../references/eip/implementation-patterns.md` for full patterns and file paths.

Based on the key specification changes from Step 1, determine the primary change type and follow the corresponding pattern:

| Spec mentions                 | Pattern to follow                                                                |
| ----------------------------- | -------------------------------------------------------------------------------- |
| New opcode                    | "Adding a new opcode" in reference doc                                           |
| New precompile                | "Adding a new precompile" in reference doc                                       |
| New transaction type          | "Adding a new transaction type" in reference doc                                 |
| Gas cost or accounting change | "Gas cost / accounting changes" in reference doc (high blast radius — 20+ files) |
| Receipt format change         | "Receipt format changes" in reference doc (consensus-critical)                   |
| New header field              | "New header field" in reference doc                                              |
| New RPC method                | "Adding new JSON-RPC methods" in reference doc                                   |
| Consensus/fork rule only      | "Adding an EIP flag to a fork" in reference doc                                  |

If the EIP spans multiple categories, combine the relevant patterns. **Check every numbered item** in each applicable pattern — items near the end (Engine API modules, capability providers, genesis JSON) are easy to miss but critical.

**Cross-reference with Step 2:** The similar EIP's `git log --name-only` output shows files the patterns doc may not list — especially test infrastructure, test base classes, and spec tests. Any file the similar EIP touched that relates to the same subsystem (EVM tests, spec tests, chain spec tests) should be in your plan too.

### Step 4 — Present Implementation Plan

**Always present the plan before writing any code.** Format it as:

```
## EIP-XXXX Implementation Plan

### Spec summary
<2-3 sentences>

### Changes required

1. **<Module name>** — <what changes>
   - File: `path/to/File.cs`
   - Action: <add / modify / create>
   - Details: <what specifically to add or change>

2. ...

### Tests
If Step 1 found test categories or vectors, map ALL of them to concrete tests (skip any already covered by existing tests found in Step 2):
- EIP test category 1 → proposed test
- EIP test category 2 → proposed test
- ...
- (additional tests beyond the EIP if needed)

### Order of implementation
1. ...
2. ...
```

Wait for the user to confirm ("yes", "go ahead", "implement it") before proceeding.
If the user asks for the plan only, stop after presenting it.

### Step 5 — Implement

Follow the plan step by step. For each file:

1. Read the file first to understand existing patterns.
2. Make the minimal, focused change needed.

**Completion gate:** After implementing, cross-check every file listed in Step 4's plan against your actual changes. If a planned file was not touched, either implement it now or explain why it was dropped. Do not proceed to Step 6 with unimplemented plan items — the plan exists because those files are needed.

**Pattern verification (mandatory):** For every file in the applicable pattern checklist from `../references/eip/implementation-patterns.md`, you must **read the file** and confirm your changes are present. Do not mark any item as "pre-existing," "not applicable," or "already handled" without opening the file and verifying. Dismissing pattern items without reading the file is the #1 cause of incomplete implementations — missing Engine API methods, capability registration, or payload versioning will break CL↔EL communication.

#### Backward compatibility with prerequisite EIPs

EIPs build on prerequisites — e.g., an EIP may assume EIP-1559 base fee fields already exist. The new EIP's feature flag controls **only the new behavior**. When implementing:

- **Guard new behavior behind `IsEip{number}Enabled`**, but do NOT wrap prerequisite logic that already exists under its own flag. The new flag must be additive — when it is `false`, the client must behave identically to before.
- **Never assume the new flag is the only one that matters.** If the EIP spec says "the access list from EIP-2930 is extended with...", the access-list logic is already gated by its own flag. Your code adds to it; it does not replace or re-gate it.
- **Test the flag-off path explicitly.** Create at least one test where `IsEip{number}Enabled = false` and verify that all existing behavior (gas costs, validation, header fields, tx processing) is unchanged. This catches accidental coupling where new code runs even when the flag is off.
- **Check call sites that branch on related flags.** If you add a new field to `BlockHeader` or `Transaction`, search for every place the related prerequisite fields are read and ensure your addition does not alter those code paths when your flag is off.

After all code changes, add or update tests:

- **ALWAYS** use `Prepare.EvmCode` fluent builder for test bytecode — never construct byte arrays manually
- **ALWAYS** match the test base class to the EIP category: `VirtualMachineTestsBase` for opcodes/gas, `BlockchainTestBase` for block-level behavior (withdrawals, tx types, state changes), `PrecompileTests<T>` for precompiles — follow the similar EIP's test from Step 2
- **If the EIP changes existing behavior** (gas costs, validation rules), you **MUST** test both enabled and disabled states using `OverridableReleaseSpec` — for new-only features (new opcode, new precompile), enabled-only is sufficient

### Step 6 — Verify

Build, run relevant tests, and format. If build or tests fail → fix → repeat.

#### Test scope — all related EIP tests, not just yours

Your EIP may share validation logic, gas accounting, or data structures with prerequisite and sibling EIPs. A seemingly isolated change can break tests for EIPs you didn't touch.

- Run the full test project for each module you touched, not just your new test file.
- If that takes too long, at minimum also run tests whose names match EIPs listed in the `Requires` header from Step 1.
- If any pre-existing test fails, do NOT skip or modify it without understanding why. A failing prerequisite-EIP test means you broke backward compatibility — fix your implementation, not the old test.

### Step 7 — Self-Review Loop

Before claiming the implementation is complete, launch **two subagent reviews in parallel**:

1. **Spec compliance** — provide the EIP number, spec text (and prerequisites from Step 1), diff of all changed files, and the `eip-implementation-reviewer` skill. This checks that the implementation matches the spec and tests cover all mandated behaviors.

2. **Code quality** — provide the diff and the `review` skill. This checks consensus correctness, security, robustness, performance, DI patterns, and breaking changes.

**Fix-and-verify loop (mandatory):**

If either review returns CRITICAL or HIGH findings:

1. For each finding: apply the fix in code
2. Re-run Step 6 (build + tests) to verify the fix doesn't break anything
3. Re-launch **only the review that produced the finding** with a fresh diff
4. Repeat until both reviews return APPROVE or APPROVE WITH CONDITIONS (MEDIUM/LOW only)

**Do NOT** claim the implementation is complete while any CRITICAL or HIGH finding is unresolved. Do NOT skip the re-review — fixing code without re-reviewing is the same as not reviewing at all.

**Max iterations:** 3. If findings persist after 3 fix-and-verify cycles, stop and present the remaining findings to the user for guidance.

## Edge cases

- **WebFetch failure**: Try the fallback URL, then ask the user for a direct link
- **Partially implemented EIP**: Step 2 checks for this — if found, adapt the plan to build on existing work

## Common mistakes

| Mistake                                | Fix                                                                                                                                                      |
| -------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Writing code before user approves plan | Always wait for explicit confirmation after Step 4                                                                                                       |
| Forgetting flag registration files     | EIP flags need 10 files across 5 layers, not just IReleaseSpec — see reference doc                                                                       |
| Skipping build verification            | Always run Step 6 before claiming implementation is complete                                                                                             |
| Breaking unrelated tests               | If your EIP changes gas costs or tx validation, **check whether existing tests break** — disable the flag in affected tests via `OverridableReleaseSpec` |
| Breaking behavior when new flag is off | New code must be fully gated behind `IsEip{number}Enabled` — test the flag-off path to confirm existing behavior is unchanged                            |
| Only running new test file             | Run the full test project for each touched module; prerequisite EIP tests can break silently                                                             |
| Skipping self-review                   | Always run Step 7 — the implementation author misses spec gaps that a fresh review catches                                                               |

## References

- **Implementation patterns + file paths**: See `../references/eip/implementation-patterns.md`
