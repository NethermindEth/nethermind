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
