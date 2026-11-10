---
name: fork-creator
description: Use when the user asks to create a new Ethereum hard fork in Nethermind, e.g. "create fork X", "add a new fork after Prague", or when the eip-implementer skill determines the target fork doesn't exist.
allowed-tools: [Bash(dotnet build*), Bash(dotnet test*), Read, Grep, Glob, Edit, Write]
---

# Fork Creator

Creates a new hard fork in the Nethermind codebase.

## Progress tracker

```
Fork creation:
- [ ] Step 1: Gather fork details from user
- [ ] Step 2: Explore current fork state
- [ ] Step 3: Load relevant `.agents/rules/` per Codebase Rules, then create fork scaffolding
- [ ] Step 4: Build and verify
```

## Workflow

### Step 1 — Gather Fork Details

Ask the user for:
1. **Fork name** (e.g., "Amsterdam")
2. **Parent fork** — default: the highest-numbered fork in `Nethermind.Specs/Forks/`
3. **Released or unreleased?** — Default: unreleased. This controls the activation timestamp and which files to touch.
4. **Engine API versions** — does the fork introduce new `engine_newPayload` / `engine_getPayload` / `engine_forkchoiceUpdated` versions?

### Step 2 — Explore Current Fork State

1. List fork files in `src/Nethermind/Nethermind.Specs/Forks/` — determine the next number prefix.
2. Read the parent fork file and the most recent fork file (e.g., `26_Bogota.cs`) to learn the current pattern.
3. Read `Nethermind.Specs/MainnetSpecProvider.cs` — timestamp constants, `{Name}Activation` properties and the `ForkSchedule` initializer.
4. Find every place that lists the most recent fork by name: `grep -rn "<LatestFork>.Instance\|nameof(<LatestFork>)\|ProtocolFork.<LatestFork>" src/Nethermind --include=*.cs`.

### Step 3 — Create Fork Scaffolding

**Always create:**

1. **Fork file** — `Nethermind.Specs/Forks/{NN}_{Name}.cs`:
   ```csharp
   public class {Name}() : NamedReleaseSpec<{Name}>({Parent}.Instance)
   {
       public override void Apply(NamedReleaseSpec spec)
       {
           spec.Name = "{Name}";
           // spec.IsEipXXXXEnabled = true;
           // spec.EngineApiNewPayloadVersion = EngineApiVersions.NewPayload.VN;
       }
   }
   ```
   Set the `EngineApi*Version` properties when the fork bumps an Engine API version (constants live in `Nethermind.Core/EngineApiVersions.cs`).

2. **`MainnetSpecProvider.cs`**:
   - Add `{Name}BlockTimestamp` — the real value if released. Unreleased forks use distinct sentinels near `ulong.MaxValue` (e.g., Amsterdam `ulong.MaxValue - 1`, Bogota `ulong.MaxValue`), ordered by fork. Never reuse an existing sentinel; shift the later unreleased sentinels if needed.
   - Add the `{Name}Activation` static property
   - Add `[{Name}BlockTimestamp] = {Name}.Instance` to the `ForkSchedule` initializer

3. **`Nethermind.Specs/SpecNameParser.cs`** — map `nameof({Name})` to `{Name}.Instance`.

4. **Other fork-name lists** found in Step 2.4 (e.g., `Nethermind.Stateless.Executor/IO/ProtocolFork.cs`, test fork aliases) — add the new fork where the previous fork is listed.

**If the fork adds Engine API versions:** add `EngineRpcModule.{Name}.cs` and `IEngineRpcModule.{Name}.cs` in `Nethermind.Merge.Plugin/` (partial class per fork), and register the methods in `Handlers/EngineRpcCapabilitiesProvider.cs`.

**Only for released forks — also update:**

5. `Nethermind.Specs/Forks/Fork.cs` — `GetLatest()` returns `{Name}.Instance` (set by hand)
6. `src/Nethermind/Chains/foundation.json` — add transition timestamps for EIPs enabled in this fork. `ForkTests.GetLatest_Matches_FoundationJson` checks that `Fork.GetLatest()` matches the last transition in this file.
7. `Nethermind.Specs.Test/ChainSpecStyle/ChainSpecBasedSpecProviderTests.cs` — add activation test cases

**Do NOT touch files 5-7 for unreleased forks** — they reflect released mainnet state.

### Step 4 — Build and Verify

Build, then run `Nethermind.Specs.Test`. If tests fail, fix and repeat.
