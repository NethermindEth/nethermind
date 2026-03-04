# Nethermind EIP Component Mapping

## EIP Category → Primary Modules

| EIP Category | Primary Modules | Key Files |
|---|---|---|
| New opcode (Core/EVM) | `Nethermind.Evm`, `Nethermind.Specs` | `Instruction.cs`, `EvmInstructions.*.cs`, `GasCostOf.cs`, `IReleaseSpec.cs` |
| New precompile (Core) | `Nethermind.Evm.Precompiles`, `Nethermind.Specs` | New precompile class, `Extensions.cs`, `ReleaseSpec.cs`, `IReleaseSpec.cs` |
| New transaction type (Core) | `Nethermind.Core`, `Nethermind.Serialization.Rlp`, `Nethermind.TxPool` | `TxType.cs`, `Transaction.cs`, new decoder in `TxDecoders/`, `TxDecoder.cs` |
| Hard fork / consensus rule | `Nethermind.Specs`, `Nethermind.Core` | `IReleaseSpec.cs`, `ReleaseSpec.cs`, fork file in `Forks/`, `MainnetSpecProvider.cs` |
| New JSON-RPC method | `Nethermind.JsonRpc`, `Nethermind.Facade` | Module interface + implementation in `Modules/<Name>/` |
| p2p / networking | `Nethermind.Network`, `Nethermind.Network.Discovery` | Protocol handler classes |
| State / storage rule | `Nethermind.State`, `Nethermind.Trie` | `WorldState.cs`, trie node files |
| PoS / Engine API | `Nethermind.Merge.Plugin` | `EngineRpcModule.cs`, execution payload classes |
| Blob / EIP-4844 style | `Nethermind.Core`, `Nethermind.Evm`, `Nethermind.TxPool` | Blob tx fields, validation |

## Key File Paths

### Specs
- `Nethermind.Core/Specs/IReleaseSpec.cs` — EIP feature flags interface
- `Nethermind.Specs/ReleaseSpec.cs` — mutable implementation; `BuildPrecompilesCache()` here
- `Nethermind.Specs/Forks/` — one file per fork (e.g., `18_Prague.cs`, `17_Cancun.cs`)
- `Nethermind.Specs/MainnetSpecProvider.cs` — maps timestamps/block numbers to fork specs

### EVM
- `Nethermind.Evm/Instruction.cs` — `enum Instruction : byte` + `InstructionExtensions`
- `Nethermind.Evm/Instructions/EvmInstructions.cs` — opcode dispatch table
- `Nethermind.Evm/Instructions/EvmInstructions.*.cs` — partial classes by category (Math, Storage, Call, Bitwise, Stack, etc.)
- `Nethermind.Evm/GasCostOf.cs` — gas cost constants

### Precompiles
- `Nethermind.Evm/Precompiles/IPrecompile.cs` — interface
- `Nethermind.Evm.Precompiles/*.cs` — one file per precompile
- `Nethermind.Evm.Precompiles/Extensions.cs` — `ListPrecompiles()` registration

### Transactions
- `Nethermind.Core/TxType.cs` — `enum TxType : byte`
- `Nethermind.Core/Transaction.cs` — transaction fields
- `Nethermind.Serialization.Rlp/TxDecoder.cs` — top-level decoder with type routing
- `Nethermind.Serialization.Rlp/TxDecoders/` — per-type decoders (`LegacyTxDecoder.cs`, `EIP1559TxDecoder.cs`, `BlobTxDecoder.cs`, `SetCodeTxDecoder.cs`, etc.)

### JSON-RPC
- `Nethermind.JsonRpc/Modules/` — one subdirectory per module (Eth, Debug, Trace, etc.)
- `Nethermind.Facade/` — high-level facades called by RPC modules

### Tests
- `Nethermind.Evm.Test/` — EVM opcode and precompile tests
- `Nethermind.Specs.Test/` — spec/fork configuration tests
- `Nethermind.JsonRpc.Test/` — RPC module tests
- `Nethermind.TxPool.Test/` — transaction pool tests

## Fork Hierarchy (Mainnet)

```
Frontier → Homestead → TangerineWhistle → SpuriousDragon
→ Byzantium → Constantinople → ConstantinopleFix
→ Istanbul → MuirGlacier → Berlin → London → ArrowGlacier
→ GrayGlacier → GrayGlacierWithdrawals → Shanghai
→ Cancun → Prague → Osaka (future)
```

Each fork class inherits from the previous and overrides properties to enable new EIPs.

## Implementation Patterns

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
