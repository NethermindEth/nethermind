# Tests & Benchmarks

This is the single rule for all test and benchmark projects. It applies to any `*Test*` or `*Benchmark*` project under `src/Nethermind/`.

## TestBlockchain (`Nethermind.Core.Test/Blockchain/TestBlockchain.cs`)

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

## E2ESyncTests (`Synchronization.Test/E2ESyncTests.cs`)

Multi-instance setup for sync testing. Reference for setting up full component stacks through Autofac with dynamic container creation.

## Benchmark setup

For benchmarks, use production DI modules with `DiagnosticMode.MemDb` overrides. Don't manually construct `WorldState`, `TrieStore`, `BlockProcessor` etc.

Example from `Nethermind.Evm.Benchmark` (correct pattern):

```csharp
// Use production modules; override only what you need
IContainer container = new ContainerBuilder()
    .AddModule(new NethermindModule(spec, configProvider, logManager))
    .AddModule(new TestEnvironmentModule(nodeKey, null))  // wires MemDb, test logging
    .Build();
```

## DI anti-pattern — never manually new up infrastructure

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

## Pure projector exception

When the unit under test is a *pure projector* — its only behavior is reading properties from injected services and assembling a DTO, with no branching on service state, no I/O, no caching, and no orchestration — `Substitute.For<>()` on the collaborators is acceptable. The integration test that wires the projector into the rest of the system is the safety net.

A class qualifies when it satisfies all of:

- Methods read collaborator properties or call collaborator getter-only methods.
- Output is a DTO whose fields are direct or arithmetic transformations of the inputs.
- No persistence, no event publication, no logging-as-side-effect.
- No branching on collaborator return values beyond null/empty handling.

When in doubt, prefer the rule and use `TestBlockchain`. Pure projectors are uncommon — most services do enough work that real collaborators catch real bugs.

Example: `Nethermind.Mcp.Adapter.NethermindNodeAdapter` projects `IBlockTree.Head.Number`, `ISyncPeerPool.PeerCount`, etc. into MCP DTOs; substituting collaborators keeps adapter tests focused on the projection contract while `IntegrationTests` cover real-services wire-up.

## Test guidelines

- Add tests to existing test files rather than creating new ones
- **Do not duplicate test methods that differ only in parameters** — use `[TestCase(...)]` or `[TestCaseSource(...)]` to parameterize a single method
- Before writing a new test, check if an existing test can be extended with another `[TestCase]` or use `[TestCaseSource]`

## DotNetty `IByteBuffer` in tests

- Prefer `using DisposableByteBuffer` via `.AsDisposable()` for releasing `IByteBuffer` in tests
- For leak-detection tests, use `PooledBufferLeakDetector` from `Nethermind.Network.Test`
