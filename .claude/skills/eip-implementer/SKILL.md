---
name: eip-implementer
description: >
  Fetches, summarizes, and implements Ethereum Improvement Proposals (EIPs) in the
  Nethermind client codebase. Use when the user says "implement EIP-XXXX", "add support
  for EIP-XXXX", "/eip-implementer XXXX", or asks to "plan EIP-XXXX implementation".
  The skill fetches the EIP spec from https://eips.ethereum.org/EIPS/eip-{number},
  summarizes the key technical requirements, maps them to Nethermind modules, produces
  a detailed implementation plan for user approval, then implements on confirmation.
---

# EIP Implementer

## Workflow

### Step 1 — Fetch and Summarize the EIP

Fetch the EIP using `WebFetch`:

```
URL: https://eips.ethereum.org/EIPS/eip-{number}
```

Extract and present:
- **EIP number + title**
- **Category** (Core, Networking, Interface, ERC/EIP, Meta, Informational)
- **Abstract** — one-paragraph summary
- **Key specification changes** — bullet list of what the spec mandates (new opcodes, new tx type, new precompile, new RPC methods, new consensus rules, etc.)
- **Backwards compatibility** notes

### Step 2 — Map to Nethermind Components

Based on the spec changes, identify which Nethermind modules are affected.
Consult `references/nethermind-eip-mapping.md` for the canonical mapping.

### Step 3 — Present Implementation Plan

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

### Step 4 — Implement

Follow the plan step by step. For each file:
1. Read the file first to understand existing patterns.
2. Make the minimal, focused change needed.
3. Follow Nethermind coding guidelines (no LINQ over loops, no `var`, `is null`/`is not null`, `nameof`, etc.).

After all code changes, add or update tests as planned.

## Key Patterns

### Adding an EIP flag to a fork

Every EIP that needs a feature flag requires changes in **six** places:

1. Add `bool IsEipXXXXEnabled { get; }` to `Nethermind.Core/Specs/IReleaseSpec.cs`
2. Add `public bool IsEipXXXXEnabled { get; set; }` to `Nethermind.Specs/ReleaseSpec.cs`
3. Set `IsEipXXXXEnabled = true` in the appropriate fork file under `Nethermind.Specs/Forks/`
4. Update `MainnetSpecProvider.cs` if a new fork is introduced
5. **ChainSpec support** — four files so networks loaded from a chain spec JSON can independently activate the EIP:
   - `Nethermind.Specs/ChainSpecStyle/ChainParameters.cs` — add `public ulong? EipXXXXTransitionTimestamp { get; set; }`
   - `Nethermind.Specs/ChainSpecStyle/Json/ChainSpecParamsJson.cs` — add `public ulong? EipXXXXTransitionTimestamp { get; set; }`
   - `Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs` — map JSON → parameters: `EipXXXXTransitionTimestamp = chainSpecJson.Params.EipXXXXTransitionTimestamp,`
   - `Nethermind.Specs/ChainSpecStyle/ChainSpecBasedSpecProvider.cs` — activate the flag: `releaseSpec.IsEipXXXXEnabled = (chainSpec.Parameters.EipXXXXTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;`

### Adding a new opcode

1. Add enum value to `Nethermind.Evm/Instruction.cs`
2. Implement handler in the appropriate `Nethermind.Evm/Instructions/EvmInstructions.*.cs` partial class
3. Add gas cost constant to `Nethermind.Evm/GasCostOf.cs` if needed
4. Update `InstructionExtensions.StackRequirements()` in `Instruction.cs`

### Adding a new precompile

1. Create class implementing `IPrecompile<T>` in `Nethermind.Evm.Precompiles/`
2. Implement `Address`, `Name`, `BaseGasCost()`, `DataGasCost()`, `Run()`
3. Add EIP flag to `IReleaseSpec` and set it in the fork
4. Register in `ReleaseSpec.BuildPrecompilesCache()` and `Extensions.ListPrecompiles()`

### Adding a new transaction type

1. Add enum value to `Nethermind.Core/TxType.cs`
2. Add new fields to `Nethermind.Core/Transaction.cs`
3. Create decoder in `Nethermind.Serialization.Rlp/TxDecoders/` following existing decoder patterns
4. Register in `TxDecoder.cs`

### Adding new JSON-RPC methods

1. Add method signatures to the module interface in `Nethermind.JsonRpc/Modules/`
2. Implement in the corresponding module class
3. Register in the module factory if needed

## References

- **Module-to-component mapping**: See `references/nethermind-eip-mapping.md` — consult whenever mapping EIP changes to files.
