# Nethermind.Init

Initialization logic, memory management, metrics, and **DI module definitions**.

## Modules (`Modules/`)

Production Autofac modules that wire the entire application:

| Module | Purpose |
|--------|---------|
| `NethermindModule` | Top-level composition of all modules |
| `DbModule` | Database factories and stores |
| `WorldStateModule` | World state, trie, node storage |
| `BlockProcessingModule` | Transaction processors, validators, EVM |
| `NetworkModule` | Message serializers, network |

When adding new components, register them in the appropriate module rather than manually constructing. Use `AddSingleton<T>()`, `AddScoped<T>()`, `AddModule()`, `Map<>()`, `Bind<>()` from `ContainerBuilderExtensions.cs`.
