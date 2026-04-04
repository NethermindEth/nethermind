# Nethermind EIP Component Mapping

## EIP Category → Primary Modules

| EIP Category                 | Primary Modules                                                              | Key Files                                                                                |
| ---------------------------- | ---------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| New opcode (Core/EVM)        | `Nethermind.Evm`, `Nethermind.Specs`                                         | `Instruction.cs`, `EvmInstructions.*.cs`, `GasCostOf.cs`, `IReleaseSpec.cs`              |
| New precompile (Core)        | `Nethermind.Evm.Precompiles`, `Nethermind.Specs`                             | New precompile class, `Extensions.cs`, `ReleaseSpec.cs`, `IReleaseSpec.cs`               |
| New transaction type (Core)  | `Nethermind.Core`, `Nethermind.Serialization.Rlp`, `Nethermind.TxPool`       | `TxType.cs`, `Transaction.cs`, new decoder in `TxDecoders/`, `TxDecoder.cs`              |
| Gas cost / accounting change | `Nethermind.Evm`, `Nethermind.Evm.Tracing`                                   | `GasCostOf.cs`, `IGasPolicy.cs`, `TransactionProcessor.cs`, `ITxTracer.cs` + ~20 tracers |
| Receipt format change        | `Nethermind.Serialization.Rlp`, `Nethermind.State`                           | `RlpBehaviors.cs`, receipt decoders, `ReceiptTrie.cs` (consensus-critical)               |
| New header field             | `Nethermind.Core`, `Nethermind.Serialization.Rlp`, `Nethermind.Merge.Plugin` | `BlockHeader.cs`, `HeaderDecoder.cs`, `ExecutionPayload*.cs`                             |
| Hard fork / consensus rule   | `Nethermind.Specs`, `Nethermind.Core`                                        | `IReleaseSpec.cs`, `ReleaseSpec.cs`, fork file in `Forks/`, `MainnetSpecProvider.cs`     |
| New JSON-RPC method          | `Nethermind.JsonRpc`, `Nethermind.Facade`                                    | Module interface + implementation in `Modules/<Name>/`                                   |
| p2p / networking             | `Nethermind.Network`, `Nethermind.Network.Discovery`                         | Protocol handler classes                                                                 |
| State / storage rule         | `Nethermind.State`, `Nethermind.Trie`                                        | `WorldState.cs`, trie node files                                                         |
| PoS / Engine API             | `Nethermind.Merge.Plugin`                                                    | `EngineRpcModule.cs`, execution payload classes                                          |
| Blob / EIP-4844 style        | `Nethermind.Core`, `Nethermind.Evm`, `Nethermind.TxPool`                     | Blob tx fields, validation                                                               |

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

### Test infrastructure (update when adding new opcodes, header fields, or tx types)

- `Nethermind.Evm/ByteCodeBuilderExtensions.cs` — fluent bytecode builder; add a method for each new opcode (e.g., `SLOTNUM()`)
- `Nethermind.Core.Test/Builders/BlockBuilder.cs` — add `With{FieldName}()` for each new header field
- `Nethermind.Evm.Test/InvalidOpcodeTests.cs` — update fork opcode validation when adding new opcodes

## Implementation Patterns

### Adding an EIP flag to a fork

Every consensus EIP that needs a feature flag requires changes in **10 files** across 5 layers:

**Layer 1 — Core interface + implementations (3 files):**

1. `Nethermind.Core/Specs/IReleaseSpec.cs` — add `bool IsEipXXXXEnabled { get; }` with XML doc
2. `Nethermind.Specs/ReleaseSpec.cs` — add `public bool IsEipXXXXEnabled { get; set; }`
3. `Nethermind.Core/Specs/ReleaseSpecDecorator.cs` — add `public virtual bool IsEipXXXXEnabled => spec.IsEipXXXXEnabled;`

**Layer 2 — Test infrastructure (1 file):** 4. `Nethermind.Specs.Test/OverridableReleaseSpec.cs` — add `public bool IsEipXXXXEnabled { get; set; } = spec.IsEipXXXXEnabled;`

**Layer 3 — Fork definition (1 file):** 5. `Nethermind.Specs/Forks/XX_ForkName.cs` — set `IsEipXXXXEnabled = true;` in fork constructor

**Layer 4 — Chain spec pipeline (4 files):** 6. `Nethermind.Specs/ChainSpecStyle/Json/ChainSpecParamsJson.cs` — add `public ulong? EipXXXXTransitionTimestamp { get; set; }` 7. `Nethermind.Specs/ChainSpecStyle/ChainParameters.cs` — add `public ulong? EipXXXXTransitionTimestamp { get; set; }` 8. `Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs` — map: `EipXXXXTransitionTimestamp = chainSpecJson.Params.EipXXXXTransitionTimestamp,` 9. `Nethermind.Specs/ChainSpecStyle/ChainSpecBasedSpecProvider.cs` — activate: `releaseSpec.IsEipXXXXEnabled = (chainSpec.Parameters.EipXXXXTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;`

**Layer 5 — Spec provider tests (1-2 files):** 10. `Nethermind.Specs.Test/MainnetSpecProviderTests.cs` — update the fork's EIP test method to cover the new flag 11. `Nethermind.Specs.Test/ChainSpecStyle/ChainSpecBasedSpecProviderTests.cs` — if the EIP overrides a spec **value** (e.g., `MaxCodeSize`, gas constants) in `ChainSpecBasedSpecProvider`, add a test here verifying the value is set correctly when the flag activates

**If the EIP is the first in a new fork**, also update: 11. `Nethermind.Specs/MainnetSpecProvider.cs` — add activation timestamp, `TransitionActivations` entry, and spec switch expression for the new fork

**Exceptions:**

- Receipt-only EIPs may use `IReceiptSpec` instead of `IReleaseSpec`
- Networking protocol versions (e.g., eth/70) skip `IReleaseSpec` entirely — use dynamic capability registration in `MergePlugin.cs`

### Adding a new opcode

1. Add enum value to `Nethermind.Evm/Instruction.cs`
2. Implement handler in the appropriate `Nethermind.Evm/Instructions/EvmInstructions.*.cs` partial class
3. Add gas cost constant to `Nethermind.Evm/GasCostOf.cs` if needed
4. Update `InstructionExtensions.StackRequirements()` in `Instruction.cs`
5. Add fluent method to `Nethermind.Evm/ByteCodeBuilderExtensions.cs` (e.g., `.SLOTNUM()`)
6. Update `Nethermind.Evm.Test/InvalidOpcodeTests.cs` — add new opcode to the fork's valid opcode set

### Adding a new precompile

1. Create class implementing `IPrecompile<T>` in `Nethermind.Evm.Precompiles/`
2. Implement all members of `IPrecompile<T>` — **read the interface** for current requirements
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

### Gas cost / accounting changes

High blast radius — expect 20+ files. Key areas:

1. `Nethermind.Evm/GasCostOf.cs` — new gas constants
2. `Nethermind.Evm/GasPolicy/IGasPolicy.cs` — interface changes (new methods like `ConsumeStateGas`)
3. `Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs` — implementation
4. `Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs` — validation, execution budget, `Refund()`
5. `Nethermind.Evm/TransactionProcessing/GasConsumed.cs` — struct expansion
6. `Nethermind.Evm/Instructions/EvmInstructions.Storage.cs` — SSTORE cost/refund changes
7. `Nethermind.Evm/Tracing/ITxTracer.cs` — if `GasConsumed` struct shape changes, all ~20 tracer implementations must update
8. L2 compatibility: `Nethermind.Optimism/OptimismTransactionProcessor.cs`, `Nethermind.Taiko/TaikoTransactionProcessor.cs`

### Receipt format changes

Consensus-critical — changes the receipt Merkle root.

1. `Nethermind.Core/TransactionReceipt.cs` — add field to both `TxReceipt` and `TxReceiptStructRef`
2. `Nethermind.Serialization.Rlp/RlpBehaviors.cs` — add a new flag with the next available power-of-2 value (**read existing flags** to determine it)
3. `Nethermind.Serialization.Rlp/Decoders/ReceiptMessageDecoder.cs` — encode/decode when flag set
4. `Nethermind.Serialization.Rlp/Decoders/ReceiptStorageDecoder.cs` — storage format
5. `Nethermind.Serialization.Rlp/Decoders/CompactReceiptStorageDecoder.cs` — compact format
6. `Nethermind.State/Proofs/ReceiptTrie.cs` — **consensus-critical**: include flag when computing receipt root
7. `Nethermind.JsonRpc/.../ReceiptForRpc.cs` — add to JSON-RPC response
8. **Test:** add encode→decode roundtrip in `ReceiptDecoderTests.cs` with the new field present and absent

### New header field

Adds an optional field to the block header RLP encoding. If the new field is introduced by a new fork, it typically also requires a new Engine API payload version — see items 5-8.

> **Every item below is required unless proven otherwise by reading the file.** Do not skip items 8-11 because the files already exist — a pre-existing file that does not handle your new field is incomplete. Open each file end-to-end; grep-and-insert misses version methods, capability registration, and payload schema changes.

1. `Nethermind.Core/BlockHeader.cs` — add property
2. `Nethermind.Core/Block.cs` — add read-only proxy
3. `Nethermind.Serialization.Rlp/HeaderDecoder.cs` — add to encode/decode paths. Uses `requiredItems` bool array with back-propagation for field ordering
4. `Nethermind.Consensus/Validators/HeaderValidator.cs` — validate presence (when enabled) and absence (when disabled)
5. `Nethermind.Merge.Plugin/Data/ExecutionPayload*.cs` — add to Engine API payloads (may need a new `ExecutionPayloadVN.cs`)
6. `Nethermind.Consensus/Producers/PayloadAttributes.cs` — add to payload attributes, include in payload ID hash
7. `Nethermind.Facade/Eth/BlockForRpc.cs` — conditionally include in JSON-RPC responses
8. `Nethermind.Merge.Plugin/EngineRpcModule.{ForkName}.cs` — new `engine_newPayloadVN` / `engine_getPayloadVN` methods (partial class per fork)
9. `Nethermind.Merge.Plugin/IEngineRpcModule.{ForkName}.cs` — interface for the new engine API methods
10. `Nethermind.Merge.Plugin/Handlers/EngineRpcCapabilitiesProvider.cs` — register the new engine API method capabilities
11. `Nethermind.Specs/ChainSpecStyle/Json/ChainSpecGenesisJson.cs` — add genesis JSON field if the header field appears in genesis
12. **Test:** add encode→decode roundtrip in `HeaderDecoderTests.cs` with the new field present and absent
