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

## WorldState Architecture

`IWorldState` handles the EVM→State interface. Previously it also handled storage concerns, but that was extracted into `IWorldStateScopeProvider`, leaving snapshot and journaling logic in `IWorldState`.

`IWorldStateScopeProvider` is provided into each block processing context from `IWorldStateManager` manually depending on usage. Each instance of `IWorldStateScopeProvider` is shareable across different block processing contexts. These are done in:

- `MainProcessingContext`, used for the main processing context, with `IWorldStateManager.GlobalWorldState`.
- And many other places using `IWorldStateManager.CreateOverridableWorldScope` or `IWorldStateManager.CreateResettableWorldState`.

## Singleton vs Scoped

- `AddSingleton<T>()` — one instance for the lifetime of the node. Use for stateless services, caches, and shared infrastructure.
- `AddScoped<T>()` — one instance per DI lifetime scope. Use for **stateful per-block components**: `IWorldState`, `ITransactionProcessor`, `IBranchProcessor`. A new scope is opened for each block branch.

```csharp
// Correct — WorldState is scoped because it holds per-block dirty state
builder.AddScoped<IWorldState, WorldState>();

// Wrong — registering WorldState as singleton would leak state across blocks
builder.AddSingleton<IWorldState, WorldState>();
```

## Adding a new component

1. Identify which module owns the domain (see table above).
2. Register with `AddSingleton` or `AddScoped` as appropriate.
3. If the component wraps or extends an existing one, use `AddDecorator<T, TDecorator>()`.
4. If multiple implementations are composed into one, use `AddComposite<T, TComposite>()`.
5. If one type should be aliased to another already-registered type, use `Bind<TTo, TFrom>()`.
6. Never register test-specific stubs or `MemDb` overrides in a production module — put them in `TestEnvironmentModule` or `TestBlockProcessingModule`.

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

## DSL reference (from `ContainerBuilderExtensions.cs`)

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

## Test setup pattern (preferred: direct DI)

```csharp
// Preferred — use production modules directly with test overrides
IContainer container = new ContainerBuilder()
    .AddModule(new NethermindModule(spec, configProvider, logManager))
    .AddModule(new TestEnvironmentModule(nodeKey, null))
    .Build();
```

## Extending TestBlockchain for domain-specific test bases

`TestBlockchain` is a legacy wrapper. Prefer direct DI for new tests. If you do use it, always dispose with `using`:

```csharp
public class MyTestBlockchain : TestBlockchain
{
    public IMyService MyService => Container.Resolve<IMyService>();

    protected override Task<TestBlockchain> Build(Action<ContainerBuilder>? configurer = null)
    {
        return base.Build(builder =>
        {
            builder.AddSingleton<IMyService, MyService>();
            configurer?.Invoke(builder);
        });
    }
}
```

Never add test-specific code to production modules. Overrides belong in `TestEnvironmentModule`, `TestBlockProcessingModule`, or a new test module passed to `Build`.
