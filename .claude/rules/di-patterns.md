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

## Extending TestBlockchain for domain-specific test bases

Subclass `TestBlockchain` and override `Build` to inject domain-specific modules or component overrides:

```csharp
public class MyTestBlockchain : TestBlockchain
{
    public IMyService MyService => Container.Resolve<IMyService>();

    protected override Task<TestBlockchain> Build(
        ISpecProvider? specProvider = null,
        UInt256? initialValues = null,
        Action<ContainerBuilder>? configureContainer = null)
    {
        return base.Build(specProvider, initialValues, builder =>
        {
            builder.AddSingleton<IMyService, MyService>();
            configureContainer?.Invoke(builder);
        });
    }
}
```

Never add test-specific code to production modules. Overrides belong in `TestEnvironmentModule`, `TestBlockProcessingModule`, or a new test module passed to `Build`.
