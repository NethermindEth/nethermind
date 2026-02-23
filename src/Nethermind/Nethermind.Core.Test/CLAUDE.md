# Nethermind.Core.Test

## TestBlockchain (`Blockchain/TestBlockchain.cs`)

Use for integration tests: `await TestBlockchain.ForMainnet().Build()`

Provides 30+ components via DI (BlockTree, StateReader, TxPool, BlockProcessor, etc.). Don't mock these individually — use TestBlockchain.

Customization via builder:
```csharp
await TestBlockchain.ForMainnet().Build(builder => builder
    .AddSingleton<ISpecProvider>(mySpecProvider)
    .AddDecorator<ISpecProvider>((ctx, sp) => WrapSpecProvider(sp)));
```

## Test modules (`Modules/`)

- `PseudoNethermindModule` — full production wiring without init steps
- `TestBlockProcessingModule` — test-specific block processing
- `TestEnvironmentModule` — MemDb, test logging
