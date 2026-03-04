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

Fetch the EIP using `WebFetch`:

```
URL: https://eips.ethereum.org/EIPS/eip-{number}
```

If WebFetch fails, ask the user for the EIP number or a direct link.

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

Read `references/nethermind-eip-mapping.md` for full patterns and file paths.

Based on the key specification changes from Step 1, determine the primary change type and follow the corresponding pattern:

| Spec mentions | Pattern to follow |
|---|---|
| New opcode | "Adding a new opcode" in reference doc |
| New precompile | "Adding a new precompile" in reference doc |
| New transaction type | "Adding a new transaction type" in reference doc |
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
3. Follow Nethermind coding guidelines (no LINQ over loops, no `var`, `is null`/`is not null`, `nameof`, etc.).

After all code changes, add or update tests as planned.

### Step 6 — Verify

1. Build: `dotnet build src/Nethermind/Nethermind.slnx`
2. Run relevant tests: `dotnet test --project path/to/.csproj -c release -- --filter FullyQualifiedName~TestName`
3. Format: `dotnet format whitespace src/Nethermind/ --folder`
4. If build or tests fail → fix → repeat from step 1

## Edge cases

- **WebFetch failure**: Ask the user for the EIP number or a direct URL to the spec
- **Partially implemented EIP**: Step 2 checks for this — if found, adapt the plan to build on existing work

## Common mistakes

| Mistake | Fix |
|---------|-----|
| Writing code before user approves plan | Always wait for explicit confirmation after Step 3 |
| Adding `Version` attribute in PackageReference | Nethermind uses CPM — versions go in `Directory.Packages.props` |
| Using LINQ in hot paths | Use `for`/`foreach` loops per Nethermind guidelines |
| Creating new test files | Add tests to existing test files with new test cases |
| Forgetting ChainSpec support | EIP flags need 4 ChainSpec files, not just IReleaseSpec — see reference doc |
| Skipping build verification | Always run Step 5 before claiming implementation is complete |

## References

- **Implementation patterns + file paths**: See `references/nethermind-eip-mapping.md`
