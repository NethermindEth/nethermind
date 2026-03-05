---
name: eip-implementer
description: Use when the user says "implement EIP-XXXX", "add support for EIP-XXXX", "/eip-implementer XXXX", or asks to plan an EIP implementation in the Nethermind codebase.
---

# EIP Implementer

## Progress tracker

Copy and track as you go:

```
EIP-XXXX Implementation:
- [ ] Step 1: Fetch and summarize EIP spec
- [ ] Step 2: Explore codebase (similar EIPs, current fork, target files)
- [ ] Step 3: Route spec changes to implementation patterns
- [ ] Step 4: Present implementation plan (WAIT for user approval)
- [ ] Step 5: Implement changes per plan
- [ ] Step 6: Build, test, and format
```

## Workflow

### Step 1 — Fetch and Summarize the EIP

Fetch the EIP spec using `WebFetch`:

```
Primary:  https://raw.githubusercontent.com/ethereum/EIPs/refs/heads/master/EIPS/eip-{number}.md
Fallback: https://eips.ethereum.org/EIPS/eip-{number}
```

The raw GitHub URL returns full markdown with formulas and pseudocode intact. The eips.ethereum.org fallback renders HTML that WebFetch may not fully capture. If both fail, ask the user for a direct link.

Extract and present:
- **EIP number + title**
- **Category** (Core, Networking, Interface, ERC/EIP, Meta, Informational)
- **Abstract** — one-paragraph summary
- **Key specification changes** — bullet list of what the spec mandates (new opcodes, new tx type, new precompile, new RPC methods, new consensus rules, etc.)
- **Backwards compatibility** notes

### Step 2 — Explore the Codebase

Before planning, ground yourself in the current state:

1. **Find a similar EIP** — search for a recently implemented EIP in the same category (e.g., if implementing a new opcode, look at how `MCOPY` or `TLOAD` was added). Read its spec flag, EVM handler, and tests to understand the real pattern.
2. **Check current fork** — read the latest fork file in `Nethermind.Specs/Forks/` to see where the new flag should go.
3. **Check for partial work** — search for `IsEip{number}` to see if the EIP is already partially implemented.

### Step 3 — Route to Implementation Patterns

Read `references/implementation-patterns.md` for full patterns and file paths.

Based on the key specification changes from Step 1, determine the primary change type and follow the corresponding pattern:

| Spec mentions | Pattern to follow |
|---|---|
| New opcode | "Adding a new opcode" in reference doc |
| New precompile | "Adding a new precompile" in reference doc |
| New transaction type | "Adding a new transaction type" in reference doc |
| Gas cost or accounting change | "Gas cost / accounting changes" in reference doc (high blast radius — 20+ files) |
| Receipt format change | "Receipt format changes" in reference doc (consensus-critical) |
| New header field | "New header field" in reference doc |
| New RPC method | "Adding new JSON-RPC methods" in reference doc |
| Consensus/fork rule only | "Adding an EIP flag to a fork" in reference doc |

If the EIP spans multiple categories, combine the relevant patterns.

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
- Add test in `<TestProject>/<ExistingTestFile>.cs`
- Test case: <what to assert>

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

After all code changes, add or update tests:
- **ALWAYS** use `Prepare.EvmCode` fluent builder for test bytecode — never construct byte arrays manually
- **ALWAYS** match the test base class to the EIP category: `VirtualMachineTestsBase` for opcodes/gas, `PrecompileTests<T>` for precompiles — follow the similar EIP's test from Step 2
- **If the EIP changes existing behavior** (gas costs, validation rules), you **MUST** test both enabled and disabled states using `OverridableReleaseSpec` — for new-only features (new opcode, new precompile), enabled-only is sufficient

### Step 6 — Verify

Build, run relevant tests, and format. If build or tests fail → fix → repeat.

## Edge cases

- **WebFetch failure**: Try the fallback URL, then ask the user for a direct link
- **Partially implemented EIP**: Step 2 checks for this — if found, adapt the plan to build on existing work

## Common mistakes

| Mistake | Fix |
|---------|-----|
| Writing code before user approves plan | Always wait for explicit confirmation after Step 4 |
| Forgetting flag registration files | EIP flags need 9 files across 5 layers, not just IReleaseSpec — see reference doc |
| Skipping build verification | Always run Step 6 before claiming implementation is complete |
| Breaking unrelated tests | If your EIP changes gas costs or tx validation, **check whether existing tests break** — disable the flag in affected tests via `OverridableReleaseSpec` |

## References

- **Implementation patterns + file paths**: See `references/implementation-patterns.md`
