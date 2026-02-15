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

## Cross-block Cache Performance Playbook (Session-derived)

This section captures the operational workflow and correctness gates for optimizing cross-block state caching and prewarmer throughput.

### Scope and reference commits

- Reth reference commits:
  - `c9169705e2` - initial cross-block caching
  - `628ac03c6a` - hierarchical storage cache follow-up
- Nethermind focus area:
  - `src/Nethermind/Nethermind.Consensus/Processing/BlockCachePreWarmer.cs`
  - `src/Nethermind/Nethermind.State/PreBlockCaches.cs`
  - `src/Nethermind/Nethermind.State/PrewarmerScopeProvider.cs`
  - `src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs`

### Hard correctness constraints

- Preserve tx nonce progression semantics for same-sender transactions in prewarmer.
- Naive removal of sender sequencing is unsafe:
  - Prior session attempts that removed sender grouping caused runtime failures (`InvalidTransactionException`, nonce too high) during block replay.
  - These runs also produced unrealistic MGas/s (~10x), which is invalid.
- `ApplyBlockDeltasToWarmCache()` must run only after prewarmer completion for each block.
- Fork/reorg safety must remain:
  - If `parent.Hash != LastProcessedBlockHash`, clear warm caches before next prewarm.

### Approved optimization pattern (safe high-performance)

- Favor per-thread scope reuse over per-item/per-group scope creation.
- If changing scheduler away from explicit sender groups, keep sender affinity:
  - Same sender must be routed to exactly one processing lane.
  - Lane order must preserve block tx order for that sender.
- Optimize allocations in hot path:
  - Prefer pooled arrays / low-allocation partitioning.
  - Avoid per-block dictionary-of-lists churn when possible.

### Mandatory local validation (before VM)

Run at minimum:

```bash
dotnet build src/Nethermind/Nethermind.Consensus/Nethermind.Consensus.csproj -c Release
dotnet test --project src/Nethermind/Nethermind.Consensus.Test/Nethermind.Consensus.Test.csproj -c Release -v minimal
dotnet test --project src/Nethermind/Nethermind.State.Test/Nethermind.State.Test.csproj -c Release -v minimal --filter FullyQualifiedName~StorageProviderTests
dotnet run --project src/Nethermind/Nethermind.Benchmark/Nethermind.Benchmark.csproj -c Release -- --filter "*PreBlockCacheReuseBenchmarks*"
```

### CI gate before VM

- Trigger/check `nethermind-tests.yml` before VM benchmarking when credentials are available:
  - https://github.com/NethermindEth/nethermind/actions/workflows/nethermind-tests.yml
- If triggering is unavailable in the current environment (missing token/CLI), explicitly report that limitation before proceeding.

### VM benchmark workflow (authoritative)

SSH:

```bash
/c/Windows/System32/OpenSSH/ssh.exe -i "C:\Users\kamil\.ssh\id_rsa" -o StrictHostKeyChecking=no ubuntu@51.68.103.177
```

Build image from branch head (must include pushed commits):

```bash
sudo bash -c 'cd /home/ubuntu/nethermind && git fetch origin && git checkout perf/cross-block-state-caching --force && git reset --hard origin/perf/cross-block-state-caching && docker build -t block-stm . && docker tag block-stm nethermindeth/nethermind:block-stm'
```

Run scenario:

```bash
sudo bash -c 'cd /mnt/sda/expb-data && expb execute-scenarios --config-file nethermind-only.yaml --per-payload-metrics --per-payload-metrics-logs --print-logs'
```

Collect output directory and validity metrics:

```bash
sudo ls -t /mnt/sda/expb-data/outputs/ | head -1
sudo grep -c Exception /mnt/sda/expb-data/outputs/<DIR>/nethermind.log
sudo grep -c InvalidStateRoot /mnt/sda/expb-data/outputs/<DIR>/nethermind.log
sudo grep -c Processed /mnt/sda/expb-data/outputs/<DIR>/nethermind.log
sudo python3 /tmp/calc_mgas.py /mnt/sda/expb-data/outputs/<DIR>/k6.log
```

### VM result acceptance criteria

- Must have:
  - `Exception == 0`
  - `InvalidStateRoot == 0`
  - `Processed > 0`
- Reject run as invalid if:
  - Exceptions are present, or
  - MGas/s appears unrealistically high (for example ~10x historical baseline).
- Performance objective for this scenario:
  - Track whether average MGas/s exceeds `1000`.
  - Always report this explicitly after each valid run.

### Branch hygiene for reproducible VM runs

- Always push branch commits before running VM benchmark.
- VM build command resets to `origin/perf/cross-block-state-caching`; unpushed local changes are not benchmarked.
- Record in reports:
  - branch name
  - commit SHA
  - VM output directory
  - Exception / InvalidStateRoot / Processed counts
  - MGas/s summary (count, avg, median, min, max, p10, p90)

### Regression protocol when optimization fails

- If blockchain replay fails or logs show invalid tx/state behavior:
  - revert unsafe scheduler changes immediately
  - restore last known-good behavior
  - rerun local tests and VM validation
- Prefer incremental, measurable changes over large unsafe deltas unless a full redesign is explicitly requested.
