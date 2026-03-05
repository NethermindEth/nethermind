---
name: fork-creator
description: Use when the user asks to create a new Ethereum hard fork in Nethermind, e.g. "create fork X", "add a new fork after Prague", or when the eip-implementer skill determines the target fork doesn't exist.
allowed-tools: [Read, Grep, Glob, Edit, Write]
---

# Fork Creator

Creates a new hard fork in the Nethermind codebase.

## Progress tracker

```
Fork creation:
- [ ] Step 1: Gather fork details from user
- [ ] Step 2: Explore current fork state
- [ ] Step 3: Create fork scaffolding
- [ ] Step 4: Build and verify
```

## Workflow

### Step 1 — Gather Fork Details

Ask the user for:
1. **Fork name** (e.g., "Amsterdam")
2. **Released or unreleased?** — Default: unreleased. This controls timestamp handling and which files to touch.

### Step 2 — Explore Current Fork State

1. List fork files in `src/Nethermind/Nethermind.Specs/Forks/` — determine the parent fork (highest number prefix) and the next number.
2. Read the parent fork file to learn the current pattern (class structure, inheritance, constructor style).
3. Read `MainnetSpecProvider.cs` to understand the activation constants, switch expression, and `TransitionActivations` array.

### Step 3 — Create Fork Scaffolding

**Always create:**

1. **Fork file** — `Nethermind.Specs/Forks/{NN}_{Name}.cs`
   - Follow the same pattern as the parent fork file from Step 2
   - If **unreleased**: set `Released = false` in constructor (the base class defaults to `true`)

2. **MainnetSpecProvider.cs** updates:
   - Add timestamp constant: real value if released, `ulong.MaxValue` if unreleased
   - Add to `GetSpec` switch expression
   - Add `{Name}Activation` static property
   - Add to `TransitionActivations` array

**Only for released forks — also update:**

3. `Chains/foundation.json` — Add transition timestamps for EIPs enabled in this fork
4. `Nethermind.Specs.Test/ForkTests.cs` — Update `GetLatest` test
5. `Nethermind.Specs.Test/ChainSpecStyle/ChainSpecBasedSpecProviderTests.cs` — Add activation test cases

**Do NOT touch files 3-5 for unreleased forks** — they reflect released mainnet state.

### Step 4 — Build and Verify

Run build and relevant spec tests. If tests fail, fix and repeat.

## Key Concept: Released vs Unreleased

`Fork.GetLatest()` in `Nethermind.Specs/Forks/Fork.cs` uses reflection to find the most-derived `INamedReleaseSpec` where `Released == true`. Setting `Released = false` prevents the fork from being returned, which means:
- `ForkTests.GetLatest_Returns_X` stays on the last released fork — no update needed
- `ChainSpecBasedSpecProviderTests` activation cases stay unchanged
- `foundation.json` stays unchanged
