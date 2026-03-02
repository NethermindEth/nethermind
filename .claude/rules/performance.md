---
paths:
  - "src/Nethermind/**/*.cs"
---

# Performance Patterns

## Patterns used in this codebase

- **Ref structs** for hot-path state (`EvmStack`, `EvmPooledMemory`) — avoids heap allocation
- **`Span<byte>` and `stackalloc`** for temporary buffers
- **SIMD types** (`Vector256<byte>`, `Vector128<byte>`) for bulk memory operations
- **`[MethodImpl(MethodImplOptions.AggressiveInlining)]`** on hot methods
- **`ZeroPaddedSpan`** (readonly ref struct) for zero-copy padded data
- **Function pointers** (`delegate*`) for opcode dispatch instead of virtual calls
- **Generic struct constraints** (`where T : struct, IGasPolicy<T>`) for zero-cost abstraction — enables JIT specialization per type
- **`GC.AllocateUninitializedArray<byte>(length, pinned: true)`** for pinned arrays avoiding GC relocation
- **Bool returns for error conditions** in hot paths (no exceptions for out-of-gas)