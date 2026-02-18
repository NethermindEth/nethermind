# AGENTS instructions

This guide helps to get started with the Nethermind Ethereum execution client repository. It covers the project structure, how to build and test the code, and follow the PR workflow.

## Repo structure

- [src/Nethermind](./src/Nethermind/): The Nethermind codebase
- [tools](./tools/): Various servicing tools for testing, monitoring, etc.
- [scripts](./scripts/): The build scripts and stuff used by GitHub Actions workflows
- See [README.md](./README.md) for more info

## Coding guidelines and style

- Do follow the [CONTRIBUTING.md](./CONTRIBUTING.md) guidelines
- Do follow the [.editorconfig](./.editorconfig) rules
- Do prefer low-allocation code patterns
- Prefer the latest C# syntax and conventions
- Prefer file-scoped namespaces (for existing files, follow their style)
- Prefer pattern matching and switch expressions over the traditional control flow
- Use the `nameof` operator instead of string literals for member references
- Use `is null` and `is not null` instead of `== null` and `!= null`
- Use `?.` null-conditional operator where applicable
- Use the `ArgumentNullException.ThrowIfNull` method for null checks and other similar methods
- Use the `ObjectDisposedException.ThrowIf` method for disposal checks
- Use documentation comments for all public APIs with proper structure
- Avoid `var` when declaring variables, the only acceptable exceptions are very long type names e.g. nested generic types
- Consider performance implications in high-throughput paths
- Trust null annotations, do not add redundant null checks
- When fixing a bug, always add a regression test that fails without the fix and passes with it
- Add tests to existing test files rather than creating new ones
- When adding multiple, similar tests write one test with test cases
- When adding a test, check if previous tests can be reused with new test case
- Code comments must explain _why_, not _what_
- **NEVER suggest using LINQ (`.Select()`, `.Where()`, `.Any()`, etc.) when a simple `foreach` or `for` loop would work.** LINQ has overhead and is less readable for simple iterations. Use LINQ only for complex queries where the declarative syntax significantly improves clarity.
- Keep changes minimal and focused: do not rename variables, reformat surrounding code, or refactor unrelated logic as part of a fix. Touch only what is necessary to solve the problem.
- Follow DRY: after making changes, review the result for duplicated logic. Extract repeated blocks (roughly 5+ lines) into shared methods, but do not over-extract trivial one-liners into their own methods.
- In generic types, move methods that do not depend on the type parameter to a non-generic base class or static helper to avoid redundant JIT instantiations per closed type.
- Do not use the `#region` and `#endregion` pragmas
- Do not alter anything in the [src/bench_precompiles](./src/bench_precompiles/) and [src/tests](./src/tests/) directories

## Project structure

The codebase in [src/Nethermind](./src/Nethermind/) is organized into three independent solutions:

- [Nethermind.slnx](./src/Nethermind/Nethermind.slnx): The Nethermind client codebase and tests
- [EthereumTests.slnx](./src/Nethermind/EthereumTests.slnx): The Ethereum Foundation test suite
- [Benchmarks.slnx](./src/Nethermind/Benchmarks.slnx): Performance benchmarking

### Architecture

- **Entry point and initialization**
  - [Nethermind.Runner](./src/Nethermind/Nethermind.Runner/): The app entry point and startup orchestration
  - [Nethermind.Init](./src/Nethermind/Nethermind.Init/): Initialization logic, memory management, metrics
- **General API**
  - [Nethermind.Api](./src/Nethermind/Nethermind.Api/): Core API interfaces and plugin API
  - [Nethermind.Config](./src/Nethermind/Nethermind.Config/): Configuration handling
  - [Nethermind.Logging](./src/Nethermind/Nethermind.Logging/): Logging
- **Consensus algorithms**
  - [Nethermind.Consensus.AuRa](./src/Nethermind/Nethermind.Consensus.AuRa/): Authority round (Aura)
  - [Nethermind.Consensus.Clique](./src/Nethermind/Nethermind.Consensus.Clique/): Proof of Authority (PoA)
  - [Nethermind.Consensus.Ethash](./src/Nethermind/Nethermind.Consensus.Ethash/): Proof of Work (PoW)
  - [Nethermind.Merge.Plugin](./src/Nethermind/Nethermind.Merge.Plugin/): Proof of Stake (PoS)
- **Core blockchain**
  - [Nethermind.Blockchain](./src/Nethermind/Nethermind.Blockchain/): Block processing, chain management, validators
  - [Nethermind.Core](./src/Nethermind/Nethermind.Core/): Foundational types
  - [Nethermind.Crypto](./src/Nethermind/Nethermind.Crypto/): Core cryptographic algorithms
  - [Nethermind.Evm](./src/Nethermind/Nethermind.Evm/): EVM implementation
  - [Nethermind.Evm.Precompiles](./src/Nethermind/Nethermind.Evm.Precompiles/): EVM precompiled contracts
  - [Nethermind.Specs](./src/Nethermind/Nethermind.Specs/): Network specifications and hard fork rules
- **State and storage:**
  - [Nethermind.Db](./src/Nethermind/Nethermind.Db/): Database abstraction layer
  - [Nethermind.Db.Rocks](./src/Nethermind/Nethermind.Db.Rocks/): RocksDB implementation (primary storage backend)
  - [Nethermind.State](./src/Nethermind/Nethermind.State/): World state management, accounts, contract storage
  - [Nethermind.Trie](./src/Nethermind/Nethermind.Trie/): Merkle Patricia trie implementation
- **Networking:**
  - [Nethermind.Network](./src/Nethermind/Nethermind.Network/): devp2p protocol implementation
  - [Nethermind.Network.Discovery](./src/Nethermind/Nethermind.Network.Discovery/): Peer discovery
  - [Nethermind.Network.Dns](./src/Nethermind/Nethermind.Network.Dns/): DNS-based node discovery
  - [Nethermind.Network.Enr](./src/Nethermind/Nethermind.Network.Enr/): Ethereum Node Records (ENR) handling
  - [Nethermind.Synchronization](./src/Nethermind/Nethermind.Synchronization/): Block synchronization strategies (fast sync, snap sync)
  - [Nethermind.UPnP.Plugin](./src/Nethermind/Nethermind.UPnP.Plugin/): UPnP support
- **Transaction management:**
  - [Nethermind.TxPool](./src/Nethermind/Nethermind.TxPool/): Transaction pool (mempool) management, validation, sorting
- **RPC and external interface:**
  - [Nethermind.Facade](./src/Nethermind/Nethermind.Facade/): High-level API facades for external interaction
  - [Nethermind.JsonRpc](./src/Nethermind/Nethermind.JsonRpc/): JSON-RPC server
  - [Nethermind.Sockets](./src/Nethermind/Nethermind.Sockets/): WebSocket server
- **Monitoring**
  - [Nethermind.HealthChecks](./src/Nethermind/Nethermind.HealthChecks/): Health checks
  - [Nethermind.Monitoring](./src/Nethermind/Nethermind.Monitoring/): Monitoring API
  - [Nethermind.Seq](./src/Nethermind/Nethermind.Seq/): Seq integration
- **Serialization:**
  - [Nethermind.Serialization.Json](./src/Nethermind/Nethermind.Serialization.Json/): JSON serialization
  - [Nethermind.Serialization.Rlp](./src/Nethermind/Nethermind.Serialization.Rlp/): RLP serialization
  - [Nethermind.Serialization.Ssz](./src/Nethermind/Nethermind.Serialization.Ssz/): SSZ serialization
- **Third-party integration:**
  - [Nethermind.Flashbots](./src/Nethermind/Nethermind.Flashbots/): Flashbots integration
  - [Nethermind.Optimism](./src/Nethermind/Nethermind.Optimism/): Optimism network (OP Stack) support
  - [Nethermind.Taiko](./src/Nethermind/Nethermind.Taiko/): Taiko network support
- **Tests**
  - Test suites reside in Nethermind.\*.Test directories

## Pull request guidelines

Before creating a pull request:

- Ensure the code compiles
- Add tests covering your changes and ensure they pass:
  ```bash
  dotnet test --project path/to/.csproj -c release -- --filter FullyQualifiedName~TestName
  ```
- Ensure the code is well-formatted:
  ```bash
  dotnet format whitespace src/Nethermind/ --folder
  ```
- Follow the [pull_request_template.md](.github/pull_request_template.md) format: fill in the changes section, tick the appropriate type-of-change checkboxes, and complete the testing/documentation sections. The checkboxes drive automatic PR labeling.

## Prerequisites

See [global.json](./global.json) for the required .NET SDK version.

## EVM Gas Benchmarks

The [Nethermind.Evm.Benchmark](./src/Nethermind/Nethermind.Evm.Benchmark/) project includes BenchmarkDotNet benchmarks that replay real `engine_newPayloadV4` payload files from the [gas-benchmarks](https://github.com/NethermindEth/gas-benchmarks) submodule. It is the primary tool for validating performance impact of EVM, block processing, block building, and newPayload path changes.

### Agent fast path (read this first)

For optimization work, do not recursively scan the whole repository first. Start from changed projects and map them to benchmark modes from this section, then run targeted scenarios.

- `Nethermind.Evm` / `Nethermind.Evm.Precompiles`: start with `EVMExecute`, then `EVMBuildUp`, then one block-level mode (`BlockOne` or `Block`)
- `Nethermind.Blockchain` / `Nethermind.State` / `Nethermind.Trie`: start with `BlockOne`, `Block`, then `NewPayload`
- `Nethermind.Merge.Plugin`: start with `NewPayload` and `NewPayloadMeasured`

When iterating on code, avoid `dotnet run` loops. Build once, run the benchmark executable directly.

**When to use:** After any change to the following projects, run the relevant gas benchmarks to verify there is no throughput regression:
- [Nethermind.Evm](./src/Nethermind/Nethermind.Evm/) - opcode implementations, `VirtualMachine`, instruction handling
- [Nethermind.Evm.Precompiles](./src/Nethermind/Nethermind.Evm.Precompiles/) - precompiled contracts
- [Nethermind.State](./src/Nethermind/Nethermind.State/) - world state, storage access, `WorldState`
- [Nethermind.Trie](./src/Nethermind/Nethermind.Trie/) - Merkle Patricia trie, trie store
- [Nethermind.Blockchain](./src/Nethermind/Nethermind.Blockchain/) - block processing and transaction processing paths
- [Nethermind.Merge.Plugin](./src/Nethermind/Nethermind.Merge.Plugin/) - newPayload handler flow
- [Nethermind.Specs](./src/Nethermind/Nethermind.Specs/) - gas cost changes, hard fork rules

### Setup (one-time)

```bash
git lfs install && git submodule update --init tools/gas-benchmarks
```

On Windows, if you get "Filename too long" errors, enable long paths first:

```bash
git config --global core.longpaths true
```

If the submodule was already cloned without LFS installed (genesis file shows as ~130 bytes instead of ~53MB):

```bash
git lfs install && cd tools/gas-benchmarks && git lfs pull
```

### Benchmark modes (current)

Use `--mode=<ModeName>` to select one path. Do not use legacy `--mode=EVM`.

| Mode | Benchmark class | What it measures | Typical use |
|---|---|---|---|
| `EVMExecute` | `GasPayloadExecuteBenchmarks` | Transaction execution via `TransactionProcessor.Execute` | Node-like import tx execution cost |
| `EVMBuildUp` | `GasPayloadBenchmarks` | Transaction execution via `TransactionProcessor.BuildUp` | Block-building tx execution cost |
| `BlockOne` | `GasBlockOneBenchmarks` | `BlockProcessor.ProcessOne` | Block-level processing without branch-level overhead |
| `Block` | `GasBlockBenchmarks` | `BranchProcessor.Process` | Full import branch processing overhead |
| `BlockBuilding` | `GasBlockBuildingBenchmarks` | Producer path with `ProcessingOptions.ProducingBlock` | Default block production behavior |
| `BlockBuildingMainState` | `GasBlockBuildingBenchmarks` | Producer path with `BuildBlocksOnMainState=true` | Main-state production behavior |
| `NewPayload` | `GasNewPayloadBenchmarks` | `NewPayloadHandler` path | Real handler-side newPayload flow |
| `NewPayloadMeasured` | `GasNewPayloadMeasuredBenchmarks` | Instrumented near-handler path | Detailed stage timing and breakdown |

### Running benchmarks

```bash
# Build once (incremental: only affected projects are rebuilt)
dotnet build src/Nethermind/Nethermind.Evm.Benchmark -c Release --no-restore

# Use the benchmark executable directly (no rebuild)
dotnet src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll --inprocess --mode=EVMExecute --filter "*MULMOD*"

# Quick scenario listing (without BenchmarkDotNet class tree noise)
dotnet src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll --list-scenarios --filter "*extcode*"

# Run one mode + one scenario pattern
dotnet src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll --inprocess --mode=EVMExecute --filter "*MULMOD*"
dotnet src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll --inprocess --mode=Block --filter "*a_to_a*"
dotnet src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll --inprocess --mode=NewPayloadMeasured --filter "*a_to_a*"

# Run all 8 modes for the same scenario (PowerShell)
$modes = @('EVMExecute','EVMBuildUp','BlockOne','Block','BlockBuilding','BlockBuildingMainState','NewPayload','NewPayloadMeasured')
foreach ($mode in $modes) {
  dotnet src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll --inprocess --mode=$mode --filter "*a_to_a*"
}

# Quick diagnostic mode (single payload, no BDN harness, debuggable)
dotnet src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll --diag "opcode_MULMOD-mod_bits_63"
```

### Benchmark invariants

- `BlocksConfig.CachePrecompilesOnBlockProcessing` is forced to `false` in benchmark setup. Do not override it in benchmark runs.
- Always compare baseline and candidate with identical benchmark arguments (`mode`, `filter`, warmup/iteration/launch counts).

### Faster rebuild loop

- Use project-level builds, not solution-wide builds:
  ```bash
  dotnet build src/Nethermind/Nethermind.Evm.Benchmark -c Release --no-restore
  ```
- .NET incremental build already rebuilds only affected projects and dependencies.
- If only benchmark arguments changed, skip build and rerun the executable.

### Reading results

The output includes custom columns:
- **MGas/s**: `100M gas / mean_seconds / 1M` - higher is better
- **CI-Lower / CI-Upper**: 99% confidence interval bounds for MGas/s

A regression is a drop in MGas/s outside the confidence interval. If CI intervals of before/after overlap, the difference is not statistically significant.

For `NewPayload` and `NewPayloadMeasured`, timing breakdown reports are emitted at the end of the run and persisted to files under `BenchmarkDotNet.Artifacts/results/`.

### Workflow for performance changes

1. Pick modes based on the code path you changed:
`EVMExecute` + `EVMBuildUp` for tx execution changes, `BlockOne` + `Block` for import flow changes, `BlockBuilding*` for producer flow changes, `NewPayload*` for engine API flow changes.
2. Run baseline using fixed BDN settings for comparability:
`--inprocess --warmupCount 10 --iterationCount 10 --launchCount 1`.
3. Apply your change.
4. Rerun the same command(s) with the same `--mode` and `--filter`.
5. Compare mean time, MGas/s, and allocations.
6. Add before/after numbers to your PR description.
7. Investigate any statistically meaningful drop before merge.

To run only scenarios related to your change, use `--filter` with a pattern matching opcode or scenario name. Use `--list flat --filter "*Gas*"` to discover exact names.

Test scenarios are auto-discovered from `tools/gas-benchmarks/eest_tests/testing/`. New tests added to the gas-benchmarks submodule appear automatically after `git submodule update --remote tools/gas-benchmarks`.

### CI-first test flow

When changes are ready for validation:

1. Push branch.
2. Trigger full tests on GitHub Actions (`nethermind-tests.yml`) for that branch.
3. Wait for failures, then run only failing tests locally using precise filters.
4. Apply fix, commit, push.
5. Re-run `nethermind-tests.yml` and confirm green.

If GitHub CLI is available:

```bash
gh workflow run nethermind-tests.yml --ref <branch>
gh run list --workflow nethermind-tests.yml --branch <branch> --limit 1
gh run watch <run-id>
```

For benchmark-tool changes, also run the benchmark workflow (`gas-benchmarks-bdn.yml`) in `workflow_dispatch` mode with relevant `mode` + `filter`.

### Setup/DI guidance for benchmark code

Keep benchmark setup coherent and DI-driven:

- Prefer shared setup helpers in `BlockBenchmarkHelper` over per-benchmark ad-hoc wiring.
- Reuse existing modules/components where possible, and inject only the minimal overrides required by benchmark scenarios.
- Avoid duplicating setup graphs across benchmark classes.
- Keep constructor-sensitive wiring centralized in `src/Nethermind/Nethermind.Evm.Benchmark/GasBenchmarks/BlockBenchmarkHelper.cs`.
- Gas benchmark classes should not directly instantiate `EthereumTransactionProcessor`, `BranchProcessor`, or `BlockProcessor`.

Quick maintenance check:

```bash
rg --line-number "new EthereumTransactionProcessor|new BranchProcessor|new BlockProcessor|new BeaconBlockRootHandler" src/Nethermind/Nethermind.Evm.Benchmark/GasBenchmarks
```

Expected: constructor hits only in `BlockBenchmarkHelper.cs`.
