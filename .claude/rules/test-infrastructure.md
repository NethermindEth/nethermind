---
paths:
  - "src/Nethermind/**/*Test*/**/*.cs"
  - "src/Nethermind/**/*Benchmark*/**/*.cs"
---

# Test & Benchmark Infrastructure

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

## Test guidelines

- Add tests to existing test files rather than creating new ones
- When adding similar tests, write one test with test cases (`[TestCase(...)]`)
- Check if previous tests can be reused with a new test case
- Bug fixes always need a regression test that fails without the fix
