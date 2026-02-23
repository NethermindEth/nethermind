---
paths:
  - "src/Nethermind/Nethermind.Evm/**/*.cs"
  - "src/Nethermind/Nethermind.Evm.Precompiles/**/*.cs"
---

# EVM-Specific Conventions

All EVM execution is performance-critical. Apply maximum performance discipline.

## Key files to understand first

- `VirtualMachine.cs` — main dispatch loop with `ExecuteTransaction<TGasPolicy>()`
- `Instructions/EvmInstructions.cs` — opcode lookup table generation
- `EvmStack.cs` — ref struct with SIMD operations for stack manipulation
- `EvmPooledMemory.cs` — struct-based memory with pooling
- `GasPolicy/IGasPolicy.cs` — generic gas policy interface (static abstract methods)

## Rules

- NEVER use LINQ in instruction handlers
- Use `Span<byte>` and `stackalloc` for temporary buffers
- Pass gas as `ref TGasPolicy` — never box the struct
- Return `EvmExceptionType` (or bool) for error conditions — no exceptions in hot paths
- Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on hot methods
- Use `ValueHash256` (stack-allocated), not `Hash256`, for transient hashes
- Use `KeccakCache.ComputeTo()` for hash computations
- Instructions are static methods with function pointers — maintain this pattern
- Opcode enablement is spec-driven (check `EvmInstructions.cs` for conditional registration)

## Gas policy pattern

```csharp
// Generic over TGasPolicy struct — zero boxing, JIT specialization
public struct EthereumGasPolicy : IGasPolicy<EthereumGasPolicy>
{
    public long Value;
    public static void Consume(ref EthereumGasPolicy gas, long cost) => gas.Value -= cost;
}
```

## Instruction file organization

Instructions are partitioned by category in `Instructions/`:
`Crypto`, `Stack`, `Math1Param`, `Math2Param`, `Math3Param`, `Bitwise`, `Shifts`,
`Storage`, `Call`, `Create`, `CodeCopy`, `Environment`, `ControlFlow`, `Eof`
