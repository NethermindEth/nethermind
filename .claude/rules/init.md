---
paths:
  - "src/Nethermind/Nethermind.Init/**/*.cs"
---

# Nethermind.Init

Initialization logic, memory management, metrics, and **DI module definitions**.

## Modules (`Modules/`)

Production Autofac modules that wire the entire application:

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
