# src/Nethermind — Rules index

This file is the index for folder-scoped rules. **Always-loaded** rules (coding-style, di-patterns, performance, common-review-feedback) apply to all C# in `src/Nethermind/`. The rules below add **domain-specific** guidance when editing files in those paths.

| Path glob(s) | Rule file | Domain |
|--------------|-----------|--------|
| `src/Nethermind/Nethermind.Evm/**/*.cs`, `src/Nethermind/Nethermind.Evm.Precompiles/**/*.cs` | [evm/evm-conventions.md](./evm/evm-conventions.md) | EVM, gas, instructions, precompiles |
| `src/Nethermind/Nethermind.Serialization.Rlp/**/*.cs`, `src/Nethermind/Nethermind.Serialization.Json/**/*.cs`, `src/Nethermind/Nethermind.Serialization.Ssz/**/*.cs` | [serialization.md](./serialization.md) | RLP/JSON/SSZ: span APIs, BinaryPrimitives, unchecked casts |
| `src/Nethermind/Nethermind.Blockchain/**/*.cs` | [blockchain.md](./blockchain.md) | BlockTree, validators, block processing, receipts |
| `src/Nethermind/Nethermind.State/**/*.cs`, `src/Nethermind/Nethermind.State.Flat/**/*.cs` | [state.md](./state.md) | IWorldState scope, IStateReader, IWorldStateManager |
| `src/Nethermind/Nethermind.TxPool/**/*.cs` | [txpool.md](./txpool.md) | AcceptTxResult, filter chain, blob tx pool, gossip |
| `src/Nethermind/Nethermind.Network/**/*.cs`, `src/Nethermind/Nethermind.Network.Discovery/**/*.cs`, `src/Nethermind/Nethermind.Network.Dns/**/*.cs`, `src/Nethermind/Nethermind.Network.Enr/**/*.cs` | [network.md](./network.md) | IZeroMessageSerializer, protocol versioning, P2P lifecycle |
| `src/Nethermind/Nethermind.Specs/**/*.cs` | [specs.md](./specs.md) | ReleaseSpec, ISpecProvider, test spec providers, ChainSpec JSON |
| `src/Nethermind/Nethermind.Init/**/*.cs` | [init.md](./init.md) | Module table, AddSingleton vs AddScoped, DSL reference |
| `src/Nethermind/**/*Test*/**/*.cs`, `src/Nethermind/**/*Benchmark*/**/*.cs` | [test-infrastructure.md](./test-infrastructure.md) | Tests and benchmarks (single rule for all test code) |
| `**/*.csproj`, `**/Directory.*.props` | [package-management.md](./package-management.md) | CPM, PackageReference |

**Concurrency rules** (`concurrency.md`) apply globally to all `src/Nethermind/**/*.cs` — not path-scoped to a single project, since shared state patterns appear everywhere.

When adding a new project with 3+ domain-specific conventions not covered by the always-loaded global rules, add a path-scoped `.claude/rules/` file and a row to this table. Keep path globs minimal and specific.
