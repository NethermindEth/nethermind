# AGENTS instructions

This guide helps to get started with the Nethermind Ethereum execution client repository. It covers the project structure, how to build and test the code, and follow the PR workflow.

## Repo structure

- [src/Nethermind](./src/Nethermind/): The Nethermind codebase
- [tools](./tools/): Various servicing tools for testing, monitoring, etc.
- [scripts](./scripts/): The build scripts and stuff used by GitHub Actions workflows
- See [README.md](./README.md) for more info

## Coding guidelines and style

- Follow [CONTRIBUTING.md](./CONTRIBUTING.md) and [.editorconfig](./.editorconfig)
- Prefer low-allocation code, latest C# syntax, pattern matching, `is null`/`is not null`
- Avoid `var` (exception: very long nested generic types)
- **No LINQ** when a simple `for`/`foreach` works — use LINQ only for complex queries
- Keep changes minimal and focused — don't touch unrelated code
- When fixing a bug, always add a regression test
- Do not alter [src/bench_precompiles](./src/bench_precompiles/) or [src/tests](./src/tests/)

> **Canonical conventions** are defined in `## Codebase Rules` below.

---

## Codebase Rules

### C# Coding Style

- Follow the `.editorconfig` rules
- Prefer the latest C# syntax and conventions
- Prefer file-scoped namespaces (for existing files, follow their style)
- Prefer pattern matching and switch expressions over traditional control flow
- Use `nameof` operator instead of string literals for member references
- Use `is null` and `is not null` instead of `== null` and `!= null`
- Use `?.` null-conditional operator where applicable
- Use `ArgumentNullException.ThrowIfNull` for null checks
- Use `ObjectDisposedException.ThrowIf` for disposal checks
- Use documentation comments for all public APIs
- Avoid `var` — spell out types (exception: very long nested generic types)
- Trust null annotations, don't add redundant null checks
- Code comments explain _why_, not _what_ — a comment that contradicts the code is worse than no comment; fix or remove it
- Non-obvious consensus rules or algorithms must reference the EIP number or Yellow Paper section
- Config keys must document their units and defaults in XML docs (e.g. is it milliseconds or seconds?)
- Keep changes minimal and focused — don't rename variables, reformat surrounding code, or refactor unrelated logic
- Follow DRY — extract repeated blocks (5+ lines) into shared methods, but don't over-extract trivial one-liners
- In generic types, move methods that don't depend on the type parameter to a non-generic base class or static helper
- Do not use `#region` / `#endregion`
- Do not alter `src/bench_precompiles/` or `src/tests/` directories

### Dependency Injection Patterns

Nethermind uses Autofac for DI with a custom DSL defined in `Nethermind.Core/ContainerBuilderExtensions.cs`.

#### Critical rules

- **NEVER manually wire components** that DI modules already register. Check `Nethermind.Init/Modules/` first.
- **For tests and benchmarks**: use production modules with overrides (e.g., `DiagnosticMode.MemDb`), not manual construction. See `TestBlockchain` and `E2ESyncTests`.

#### Production modules (`Nethermind.Init/Modules/`)

| Module | Registers | When to touch |
|--------|-----------|---------------|
| `NethermindModule` | Top-level composition — calls all other modules | Only to add a new top-level module |
| `DbModule` | `IDbFactory`, `IDbProvider`, all named databases | New database or new DB diagnostic mode |
| `WorldStateModule` | `IWorldStateManager`, `IStateReader`, trie store, node storage | New state backend or pruning strategy |
| `BlockProcessingModule` | `ITransactionProcessor`, `IBlockProcessor`, `IVirtualMachine`, validators | New transaction type or block processing hook |
| `BlockTreeModule` | `IBlockTree`, `IBlockStore`, `IHeaderStore`, `IReceiptStorage` | New chain storage or receipt backend |
| `NetworkModule` | Message serializers (Eth62–Eth69, Snap, Witness), RLPx host | New protocol version or message type |
| `DiscoveryModule` | Peer discovery, `INodeSource` | Discovery protocol changes |
| `RpcModules` | JSON-RPC modules (`Eth`, `Debug`, `Admin`, etc.) | New RPC method module |
| `PrewarmerModule` | State prewarming for block production | Prewarmer tuning |
| `BuiltInStepsModule` | Node initialization step chain | New startup step |

#### WorldState Architecture

`IWorldState` handles the EVM→State interface. Previously it also handled storage concerns, but that was extracted into `IWorldStateScopeProvider`, leaving snapshot and journaling logic in `IWorldState`.

`IWorldStateScopeProvider` is provided into each block processing context from `IWorldStateManager` manually depending on usage. Each instance of `IWorldStateScopeProvider` is NOT shareable across different block processing contexts. These are done in:

- `MainProcessingContext`, used for the main processing context, with `IWorldStateManager.GlobalWorldState`.
- And many other places using `IWorldStateManager.CreateOverridableWorldScope` or `IWorldStateManager.CreateResettableWorldState`.

#### Singleton vs Scoped

- `AddSingleton<T>()` — one instance for the lifetime of the node. Use for stateless services, caches, and shared infrastructure.
- `AddScoped<T>()` — one instance per DI lifetime scope. Use for **stateful per-block components**: `IWorldState`, `ITransactionProcessor`, `IBranchProcessor`. A new scope is opened for each block branch.

```csharp
// Correct — WorldState is scoped because it holds per-block dirty state
builder.AddScoped<IWorldState, WorldState>();

// Wrong — registering WorldState as singleton would leak state across blocks
builder.AddSingleton<IWorldState, WorldState>();
```

#### Adding a new component

1. Identify which module owns the domain (see table above).
2. Register with `AddSingleton` or `AddScoped` as appropriate.
3. If the component wraps or extends an existing one, use `AddDecorator<T, TDecorator>()`.
4. If multiple implementations are composed into one, use `AddComposite<T, TComposite>()`.
5. If one type should be aliased to another already-registered type, use `Bind<TTo, TFrom>()`.
6. Never register test-specific stubs or `MemDb` overrides in a production module — put them in `TestEnvironmentModule` or `TestBlockProcessingModule`.

#### Test modules (`Nethermind.Core.Test/Modules/`)

| Module | Purpose | File |
|--------|---------|------|
| `PseudoNethermindModule` | Full production wiring without init steps | `PseudoNethermindModule.cs` |
| `TestBlockProcessingModule` | Test-specific block processing overrides | `TestBlockProcessingModule.cs` |
| `TestEnvironmentModule` | MemDb, test logging, network config | `TestEnvironmentModule.cs` |
| `PseudoNetworkModule` | Network stack without actual sockets | `PseudoNetworkModule.cs` |

**What `TestEnvironmentModule` overrides** (relative to production):
- `IDbFactory` → `MemDbFactory` (no disk I/O)
- `ILogManager` → `TestLogManager(LogLevel.Error)` (quiet; Limbo has `IsTrace=true` which is slow)
- `ISealer` → `NethDevSealEngine` (uses the test node key)
- `ITimestamper` → `ManualTimestamper` (controllable time)
- `IChannelFactory` → `LocalChannelFactory` (in-process, no TCP)
- Various config decorators: pruning cache sizes, sync delays, max threads

**What `TestBlockProcessingModule` overrides** (relative to production):
- `ITxPool` → full `TxPool.TxPool` (not a stub)
- `IBlockPreprocessorStep` → composite including `RecoverSignatures`
- `IBlockProducer` → `TestBlockProducer` (manual trigger, no timer)
- `IBlockProducerRunner` → `StandardBlockProducerRunner`
- `IGasLimitCalculator` → `TargetAdjustedGasLimitCalculator` (scoped)

#### DSL reference (from `ContainerBuilderExtensions.cs`)

```csharp
builder
    .AddSingleton<IFoo, Foo>()          // singleton by type
    .AddScoped<IBar>()                  // scoped, resolved by type
    .AddDecorator<IFoo, FooDecorator>() // wraps existing IFoo registration
    .AddComposite<IFoo, CompositeFoo>() // aggregates all IFoo registrations
    .Bind<IFoo, IBar>()                 // alias: resolving IFoo returns IBar
    .Map<IFoo, IComposite>(c => c.Foo)  // extract a sub-component
    .AddModule(new OtherModule())       // compose modules
    ;
```

#### Test setup pattern (preferred: direct DI)

```csharp
// Preferred — use production modules directly with test overrides
IContainer container = new ContainerBuilder()
    .AddModule(new NethermindModule(spec, configProvider, logManager))
    .AddModule(new TestEnvironmentModule(nodeKey, null))
    .Build();
```

Never add test-specific code to production modules. Overrides belong in `TestEnvironmentModule`, `TestBlockProcessingModule`, or a new test module passed to `Build`.

### Performance Patterns

#### Patterns used in this codebase

- **Ref structs** for hot-path state (`EvmStack`, `EvmPooledMemory`) — avoids heap allocation
- **`Span<byte>` and `stackalloc`** for temporary buffers
- **SIMD types** (`Vector256<byte>`, `Vector128<byte>`) for bulk memory operations
- **`[MethodImpl(MethodImplOptions.AggressiveInlining)]`** on hot methods
- **`ZeroPaddedSpan`** (readonly ref struct) for zero-copy padded data
- **Function pointers** (`delegate*`) for opcode dispatch instead of virtual calls
- **Generic struct constraints** (`where T : struct, IGasPolicy<T>`) for zero-cost abstraction — enables JIT specialization per type
- **`GC.AllocateUninitializedArray<byte>(length, pinned: true)`** for pinned arrays avoiding GC relocation
- **Bool returns for error conditions** in hot paths (no exceptions for out-of-gas)

### C#/.NET Robustness

Patterns that cause silent failures, resource leaks, or deadlocks in production. These apply to all C# code in the repo.

#### Async

- **Never** use `async void` — exceptions are silently swallowed. Use `async Task`.
- **Never** call `.Result` or `.Wait()` on a `Task` inside an `async` method — deadlock or thread-pool starvation. Use `await`.
- A missing `await` on an async call silently discards the result. Only omit `await` when fire-and-forget is the **documented** intent.
- Async methods that perform I/O or network calls must accept a `CancellationToken` — otherwise they're uncooperative with graceful shutdown.

#### Resource management

- `IDisposable` / `IAsyncDisposable` objects (especially `IDb`, streams, channels) must be wrapped in `using` — otherwise they leak.
- Never swallow exceptions in an empty `catch` block — at minimum log the exception. Silent failures are the hardest to diagnose on a running node.

#### Thread safety

- Shared mutable state (caches, peer tables, chain state) modified from multiple threads must use proper synchronisation — unsynchronised access causes data races and subtle corruption.

#### Safety

- `unsafe` blocks must have a comment justifying the safety invariant — reviewers cannot verify correctness without it.
- Validate data from untrusted sources (P2P peers, RPC callers) before use — a `NullReferenceException` on external input is a crash vector.

### Tests & Benchmarks

This is the single rule for all test and benchmark projects. It applies to any `*Test*` or `*Benchmark*` project under `src/Nethermind/`.

#### TestBlockchain (`Nethermind.Core.Test/Blockchain/TestBlockchain.cs`)

Legacy wrapper around DI. Prefer direct `ContainerBuilder` + production modules for new tests. If you do use `TestBlockchain`, always dispose with `using`:

```csharp
// Basic usage — always use `using` for disposal
using TestBlockchain chain = await TestBlockchain.ForMainnet().Build();

// With customization
using TestBlockchain chain = await TestBlockchain.ForMainnet()
    .Build(builder => builder
        .AddSingleton<ISpecProvider>(mySpecProvider)
        .AddDecorator<ISpecProvider>((ctx, sp) => WrapSpecProvider(sp)));
```

**Provides**: `BlockTree`, `StateReader`, `TxPool`, `BlockProcessor`, `MainProcessingContext`, and 30+ other components — all wired via `PseudoNethermindModule`.

**Don't mock what TestBlockchain provides.** If you need `IBlockTree`, `IWorldState`, `ITransactionProcessor`, use DI instead of `Substitute.For<>()`.

#### E2ESyncTests (`Synchronization.Test/E2ESyncTests.cs`)

Multi-instance setup for sync testing. Reference for setting up full component stacks through Autofac with dynamic container creation.

#### Benchmark setup

For benchmarks, use production DI modules with `DiagnosticMode.MemDb` overrides. Don't manually construct `WorldState`, `TrieStore`, `BlockProcessor` etc.

Example from `Nethermind.Evm.Benchmark` (correct pattern):

```csharp
// Use production modules; override only what you need
IContainer container = new ContainerBuilder()
    .AddModule(new NethermindModule(spec, configProvider, logManager))
    .AddModule(new TestEnvironmentModule(nodeKey, null))  // wires MemDb, test logging
    .Build();
```

#### DI anti-pattern — never manually new up infrastructure

```csharp
// WRONG — manual construction makes the setup fragile and hard to refactor
WorldState worldState = new WorldState(new TrieStore(...), new MemDb(), LimboLogs.Instance);
ITransactionProcessor txProcessor = new TransactionProcessor(specProvider, worldState, vm, ...);
IBlockProcessor blockProcessor = new BlockProcessor(..., txProcessor, worldState, ...);
```

**Correct — use DI with targeted overrides:**

```csharp
// Unit tests: direct DI with targeted overrides
IContainer container = new ContainerBuilder()
    .AddModule(new PseudoNethermindModule(spec, configProvider, logManager))
    .AddModule(new TestEnvironmentModule(nodeKey, null))
    .Build();

// Benchmarks: production modules + DiagnosticMode.MemDb
IContainer container = new ContainerBuilder()
    .AddModule(new NethermindModule(spec, configProvider, LimboLogs.Instance))
    .AddModule(new TestEnvironmentModule(nodeKey, null))
    .Build();
```

The rule: **if production modules already wire a component, use them — don't construct it yourself**.

#### Test guidelines

- Add tests to existing test files rather than creating new ones
- When adding similar tests, write one test with test cases (`[TestCase(...)]`)
- Check if previous tests can be reused with a new test case
- Bug fixes always need a regression test that fails without the fix

### .github Workflows and Automation

Conventions for GitHub Actions, CODEOWNERS, and repo automation under `.github/`.

#### Workflows

- **Naming**: Use kebab-case; descriptive name (e.g. `nethermind-tests.yml`, `run-expb-reproducible-benchmarks.yml`).
- **Concurrency**: Prefer `concurrency: group: ${{ github.workflow }}-${{ github.ref }}` with `cancel-in-progress: true` for PR/push workflows to avoid duplicate runs.
- **Triggers**: Be explicit — `pull_request:`, `push: branches: [master]`, or `workflow_dispatch:` with inputs as needed.
- **Secrets**: Never log or echo secrets; use `${{ secrets.X }}` and restrict env vars to the job that needs them.
- **Matrix**: For test workflows, the project list in `nethermind-tests.yml` is the source of truth; keep matrix project names in sync with actual test project names (e.g. `Nethermind.Evm.Test`).
- **Runner labels**: Reproducible benchmarks use `reproducible-benchmarks`; other jobs typically use `ubuntu-latest` unless the workflow doc specifies otherwise.
- **Temporary files**: Workflows that render or generate config (e.g. benchmark config) must do so to a temp path and must not modify tracked source files.

#### Actions (composite or custom)

- Custom actions live under `.github/actions/<name>/` with `action.yaml` and scripts (e.g. `runner-setup.sh.j2`, `runner-configure.sh`).
- Scripts must be executable and safe for the runner OS (Linux unless noted).
- Prefer `actions/checkout` and standard `actions/*` where possible; document any third-party action version and reason.

#### Pull request template

- `.github/pull_request_template.md`: Fill in "Changes", tick type-of-change checkboxes, and complete Testing/Documentation sections. Checkboxes drive PR labeling; do not remove required sections.

#### CODEOWNERS

- Update `.github/CODEOWNERS` when adding new critical paths or ownership changes; keep paths and teams in sync with repo structure.

#### Notes for agents

- Do not change workflow logic (triggers, steps, matrices) without explicit user request.
- When adding a new workflow, follow existing patterns (concurrency, env, job names) and reference AGENTS.md for benchmark/reproducible-workflow specifics.

### Central Package Management (CPM)

This repo uses Central Package Management. `Directory.Packages.props` has `ManagePackageVersionsCentrally=true`.

#### Rules

- In `.csproj` files: `<PackageReference Include="Foo" />` — **NO Version attribute**
- In `Directory.Packages.props`: `<PackageVersion Include="Foo" Version="1.2.3" />`
- Adding `Version=` to a PackageReference **will break the build**

#### Adding a new dependency

1. Add `<PackageVersion Include="NewPackage" Version="x.y.z" />` to `Directory.Packages.props`
2. Add `<PackageReference Include="NewPackage" />` to the relevant `.csproj`

---

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

## Reproducible Benchmark Workflow Guidance

This repository contains a dedicated workflow for reproducible payload benchmarks:
- Workflow file: [`.github/workflows/run-expb-reproducible-benchmarks.yml`](./.github/workflows/run-expb-reproducible-benchmarks.yml)
- Main execution runner label: `reproducible-benchmarks`

### What the workflow does

- Resolves runtime inputs (branch, state layout, payload set, delay, optional extra flags).
- Selects one benchmark config file from `/mnt/sda/expb-data`.
- Builds or reuses Nethermind Docker image tag depending on branch rules.
- Renders a temporary config (does not modify source files) by:
  - replacing `<<DOCKER_TAG>>`
  - replacing `<<DELAY>>`
  - renaming scenario key `nethermind:` to a detailed scenario name
  - appending user-provided extra flags under `extra_flags:`
- Installs `expb` via `uv tool install --force --from ... expb`.
- Runs `expb execute-scenarios` with per-payload metrics and logs.
- Handles termination gracefully with cleanup grace period.
- On successful `master` push runs, caches per-payload timing aggregates extracted from the `processing_ms` table.
- On labeled PR runs, restores latest cached `master` metrics and posts a PR comment with PR vs master comparison.

### What to inspect in run output

- Inspect the `Run expb scenarios` step output first.
- Treat any Nethermind `Exception` as a high-priority issue.
- Explicitly scan logs for invalid block signals, including `Invalid Block` and `Invalid Blocks`.
- Review the end-of-run summary section with per-block timings and totals.
- Use summary timing values to derive aggregate metrics (average/mean at minimum; median/p95 when available).
- If a run fails or is terminated, check whether cleanup grace-period handling completed cleanly.

### Log structure reference

- Reference run used for structure validation:
  - Run: `https://github.com/NethermindEth/nethermind/actions/runs/22185801008`
  - Job: `https://github.com/NethermindEth/nethermind/actions/runs/22185801008/job/64159725161`
- Fetch logs with:
  ```bash
  gh run view 22185801008 --job 64159725161 --log
  ```
- GitHub job log lines are tab-separated in this shape:
  - `<job-name>\t<step-name>\t<timestamp>\t<message>`
  - Example step names in this workflow: `Print resolved inputs`, `Render benchmark config`, `Install or upgrade expb`, `Run expb scenarios`.
- `Run expb scenarios` contains mixed streams:
  - EXPB structured events like: `timestamp=... level=info event="..."`.
  - K6 progress and metric blocks (`http_req_duration`, `iteration_duration`, percentiles like `p(95)`).
  - Raw Nethermind runtime logs (received blocks, processed block timings, shutdown sequence).
  - Per-payload metrics table near the end, marked by:
    - `+---------+------------+-----------------+`
    - `| payload | gas_used   | processing_ms   |`
    - rows with payload id, gas used, processing time.
- ANSI color codes are present; when searching/parsing, strip ANSI escape sequences first.
- Some non-ASCII time-unit glyphs can appear mangled in plain terminal output, so prefer numeric metric fields when computing aggregates.

### Mandatory log checks

- Fail review if any of these appear in Nethermind logs:
  - `Exception`
  - `Invalid Block`
  - `Invalid Blocks`
- Workflow behavior requirement: any detected `Exception` in run output must fail the workflow after reporting matching lines.
- Also flag severe runtime signals if present:
  - `Unhandled`
  - `Fatal`
  - `ERROR`
- Confirm normal shutdown markers at end:
  - `Nethermind is shut down`
  - `event="Cleanup completed"`

### Notes for agents

- The benchmark config is rendered to a temporary file and removed afterward; no source config revert is required.
- For `pull_request` and `push` auto-runs, default mode is currently `halfpath + superblocks`.
- Keep benchmark-related changes isolated to the workflow and benchmark guidance unless explicitly asked otherwise.
