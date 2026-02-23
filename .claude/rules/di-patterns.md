---
paths:
  - "src/Nethermind/**/*.cs"
---

# Dependency Injection Patterns

Nethermind uses Autofac for DI with a custom DSL defined in `Nethermind.Core/ContainerBuilderExtensions.cs`.

## Critical rules

- **NEVER manually wire components** that DI modules already register. Check `Nethermind.Init/Modules/` first.
- **For tests and benchmarks**: use production modules with overrides (e.g., `DiagnosticMode.MemDb`), not manual construction. See `TestBlockchain` and `E2ESyncTests`.

## Production modules (`Nethermind.Init/Modules/`)

| Module | Registers | File |
|--------|-----------|------|
| `NethermindModule` | Top-level composition of all modules | `NethermindModule.cs` |
| `DbModule` | Database factories and stores | `DbModule.cs` |
| `WorldStateModule` | World state, trie, node storage | `WorldStateModule.cs` |
| `BlockProcessingModule` | Transaction processors, validators, EVM | `BlockProcessingModule.cs` |
| `NetworkModule` | Message serializers, network | `NetworkModule.cs` |

## Test modules (`Nethermind.Core.Test/Modules/`)

| Module | Purpose | File |
|--------|---------|------|
| `PseudoNethermindModule` | Full production wiring without init steps | `PseudoNethermindModule.cs` |
| `TestBlockProcessingModule` | Test-specific block processing overrides | `TestBlockProcessingModule.cs` |
| `TestEnvironmentModule` | MemDb, test logging, network config | `TestEnvironmentModule.cs` |

## Key DSL methods

- `AddSingleton<T>()` / `AddSingleton<T, TImpl>()` — singleton registration
- `AddScoped<T>()` — per-lifetime-scope (WorldState, TransactionProcessor)
- `AddModule(module)` — compose modules
- `Map<TTo, TFrom>(mapper)` — extract component from composite
- `Bind<TTo, TFrom>()` — alias registration
- `AddDecorator<T, TDecorator>()` — decorator pattern
- `AddComposite<T, TComposite>()` — composite pattern

## Module composition pattern

```csharp
builder
    .AddModule(new DbModule(...))
    .AddModule(new WorldStateModule(...))
    .AddModule(new BlockProcessingModule(...));
```

## Test setup pattern (preferred)

```csharp
// Use TestBlockchain — not manual wiring
var chain = await TestBlockchain.ForMainnet().Build();
// Access components through DI:
chain.BlockTree, chain.StateReader, chain.TxPool
```
