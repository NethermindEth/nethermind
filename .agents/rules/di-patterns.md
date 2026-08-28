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
| `NetworkModule` | devp2p message serializers and protocol handlers (eth, snap), RLPx host | New protocol version or message type |
| `DiscoveryModule` | Peer discovery, `INodeSource` | Discovery protocol changes |
| `RpcModules` | JSON-RPC modules (`Eth`, `Debug`, `Admin`, etc.) | New RPC method module |
| `PrewarmerModule` | State prewarming for block production | Prewarmer tuning |
| `BuiltInStepsModule` | Node initialization step chain | New startup step |

The table is not exhaustive — list `Nethermind.Init/Modules/` for the current set (e.g. flat-state, pruning, and monitoring modules are registered there too).

## WorldState Architecture

`IWorldState` handles the EVM→State interface. Previously it also handled storage concerns, but that was extracted into `IWorldStateScopeProvider`, leaving snapshot and journaling logic in `IWorldState`.

`IWorldStateScopeProvider` is provided into each block processing context from `IWorldStateManager` manually depending on usage. Each instance of `IWorldStateScopeProvider` is NOT shareable across different block processing contexts. These are done in:

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

## Block processing environment

- A graph of block processing components is usually called an "environment", or Env for short.
- In Nethermind these components are designed to:
  - Change behavior depending on usage:
    - main block processing
    - block production
    - eth_call
    - eth_simulate
    - prewarming thread
  - Not be thread safe.
  - Run many at the same time, each at a different base block.
  - Be changeable by plugins while keeping the different behaviors in different parts of the code working.
- This means:
  - Each thread needs to construct a new environment, usually pooled.
  - Each use case needs to modify the Autofac wiring to suit that use case.
  - Use-case authors should be careful not to break plugins by hard-coding block processing component implementations.
- Each environment is created by creating an Autofac child scope, modifying that child scope according to the use case, passing in a world state, and wrapping them in an env implementation (e.g. `IReadOnlyTxProcessorSource`, `IOverridableEnv`). It's usually useful to put the scope initialization in a factory implementation. For example:
  - `AutoReadOnlyTxProcessingEnvFactory` (`Nethermind.Consensus/Processing`) — the baseline: creates a resettable world state, registers it in a child scope as `IWorldStateScopeProvider`, and resolves an env that opens a world-state scope per `Build(header)` call and disposes the child scope when the env is disposed.
  - `PrewarmerEnvFactory` (`Nethermind.Consensus/Processing`) — the same shape, but wraps the resettable world state in a `PrewarmerScopeProvider` carrying `PreBlockCaches`, so reads made by prewarming transactions populate the caches used by main block processing.
  - `OverridableEnvFactory` (`Nethermind.State/OverridableEnv`) — adds use-case-specific wiring in the child scope: `AddDecorator<ICodeInfoRepository, OverridableCodeInfoRepository>()` so eth_call/eth_simulate can apply state, code, and spec overrides, wrapped in an `IOverridableEnv` that applies and resets the overrides around each scope.
- The child scope creation itself looks like this (from `AutoReadOnlyTxProcessingEnvFactory`):

  ```csharp
  IWorldStateScopeProvider worldState = worldStateManager.CreateResettableWorldState();
  ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) => builder
      .AddSingleton<IWorldStateScopeProvider>(worldState) // replaces the parent's world state for this env
      .AddSingleton<AutoReadOnlyTxProcessingEnv>());

  return childScope.Resolve<AutoReadOnlyTxProcessingEnv>();
  ```

- The env must implement `IDisposable` and dispose the child scope (e.g. `public void Dispose() => lifetimeScope.Dispose();`) — Autofac disposes every `IDisposable` component created in the scope, so any newly added disposable component is cleaned up without further changes to the env.

## Adding a new component

1. Identify which module owns the domain (see table above).
2. Register with `AddSingleton` or `AddScoped` as appropriate.
3. Ensure it resolves via the runner tests — `EthereumRunnerTests` (`Nethermind.Runner.Test`) builds the full production container for every shipped config, so a mis-registered dependency fails there.

## Replacing or modifying a component behavior

1. Simply registering another implementation via `AddSingleton` or `AddScoped` will replace the implementation entirely.
2. This applies to child scopes too, which is the mechanism in block processing environments that allows an env to change the world state instance.
3. In a lot more cases, one needs to intercept the service, in which case use `AddDecorator`.
4. There can be multiple nested decorators, for example from plugins, therefore do not rely on casting to determine the actual implementation or wrapping. Rather, your decorator should delegate to another concrete class such as a store which you can then interact with safely.
5. Never register test-specific stubs or `MemDb` overrides in a production module — put them in `TestEnvironmentModule` or `TestBlockProcessingModule`.

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

This is the canonical container setup — test-infrastructure.md references it rather than repeating it. Both unit tests and benchmarks use `PseudoNethermindModule` (wraps the full production `NethermindModule` without running init steps) plus `TestEnvironmentModule` (MemDb via `MemDbFactory`, test logging, in-process channels):

```csharp
IContainer container = new ContainerBuilder()
    .AddModule(new PseudoNethermindModule(spec, configProvider, logManager))
    .AddModule(new TestEnvironmentModule(nodeKey, null))
    .Build();
```

`TestNethermindModule` combines both in one module with a `TestSpecProvider` default — the preferred shorthand, and what `Nethermind.Evm.Benchmark` uses (`new TestNethermindModule(Osaka.Instance)`).

## Anti-pattern
- Using the form `.Add<IFoo>(ctx => new Foo(ctx.Resolve<Dep1>(), ctx.Resolve<Dep2>()))` is an anti-pattern. It will cause changes to the wiring when `Foo` adds new dependencies, which increases review load.
  - Rather, do `.Add<IFoo, Foo>()` unless unavoidable.
- Similar case with using `IComponentContext` within a class that is not related to component wiring. Rather, pass in the dependency to the construct either directly as a type, or as a `Func<IFoo>` if multiple instantiation is needed or `Lazy<IFoo>` if lazy initialization is needed.
