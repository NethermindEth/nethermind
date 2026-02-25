---
paths:
  - "src/Nethermind/**/*Test*/**/*.cs"
  - "src/Nethermind/**/*Benchmark*/**/*.cs"
---

# Tests & benchmarks (single rule for all test code)

This is the **only** folder-scoped rule for test and benchmark projects. It applies to any `*Test*` or `*Benchmark*` project under `src/Nethermind/`.

## TestBlockchain (`Nethermind.Core.Test/Blockchain/TestBlockchain.cs`)

Full blockchain environment with DI, block processing, and state management.

```csharp
// Basic usage
var chain = await TestBlockchain.ForMainnet().Build();

// With customization
var chain = await TestBlockchain.ForMainnet()
    .Build(builder => builder
        .AddSingleton<ISpecProvider>(mySpecProvider)
        .AddDecorator<ISpecProvider>((ctx, sp) => WrapSpecProvider(sp)));
```

**Provides**: `BlockTree`, `StateReader`, `TxPool`, `BlockProcessor`, `MainProcessingContext`, and 30+ other components — all wired via `PseudoNethermindModule`.

**Don't mock what TestBlockchain provides.** If you need `IBlockTree`, `IWorldState`, `ITransactionProcessor`, use `TestBlockchain` instead of `Substitute.For<>()`.

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
// Unit tests: TestBlockchain with builder overrides
var chain = await TestBlockchain.ForMainnet()
    .Build(builder => builder
        .AddSingleton<ISpecProvider>(mySpecProvider)
        .AddDecorator<IBlockProcessor, MyCustomBlockProcessor>());

// Benchmarks: production modules + DiagnosticMode.MemDb
new ContainerBuilder()
    .AddModule(new NethermindModule(spec, configProvider, LimboLogs.Instance))
    .AddModule(new TestEnvironmentModule(nodeKey, null))
    .Build();
```

The rule: **if `TestBlockchain` or production modules already wire a component, use them — don't construct it yourself**.

## Test guidelines

- Add tests to existing test files rather than creating new ones
- When adding similar tests, write one test with test cases (`[TestCase(...)]`)
- Check if previous tests can be reused with a new test case
- Bug fixes always need a regression test that fails without the fix
