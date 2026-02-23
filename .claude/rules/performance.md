---
paths:
  - "src/Nethermind/**/*.cs"
---

# Performance Patterns

## General rules

- Prefer low-allocation code patterns
- Consider performance implications in high-throughput paths
- NEVER use LINQ in hot paths — use `for`/`foreach` loops
- In generic types, move methods that don't depend on the type parameter to a non-generic base to avoid redundant JIT instantiations

## Patterns used in this codebase

- **Ref structs** for hot-path state (`EvmStack`, `EvmPooledMemory`) — avoids heap allocation
- **`Span<byte>` and `stackalloc`** for temporary buffers
- **SIMD types** (`Vector256<byte>`, `Vector128<byte>`) for bulk memory operations
- **`[MethodImpl(MethodImplOptions.AggressiveInlining)]`** on hot methods
- **Object pooling** via `ConcurrentQueue<T>` with bounded pool sizes (see `StackPool`)
- **`ValueHash256`** (stack-allocated) instead of `Hash256` in hot paths
- **`KeccakCache`** for frequently-computed hashes
- **`ZeroPaddedSpan`** (readonly ref struct) for zero-copy padded data
- **Function pointers** (`delegate*`) for opcode dispatch instead of virtual calls
- **Generic struct constraints** (`where T : struct, IGasPolicy<T>`) for zero-cost abstraction — enables JIT specialization per type
- **`GC.AllocateUninitializedArray<byte>(length, pinned: true)`** for pinned arrays avoiding GC relocation
- **Bool returns for error conditions** in hot paths (no exceptions for out-of-gas)
