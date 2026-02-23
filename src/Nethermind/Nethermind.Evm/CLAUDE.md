# Nethermind.Evm

All EVM execution is performance-critical. Apply maximum performance discipline.

Key entry points:
- `VirtualMachine.cs` — main dispatch loop, opcode function pointers
- `Instructions/EvmInstructions.cs` — opcode lookup table (spec-driven)
- `EvmStack.cs` — ref struct, SIMD (`Vector256<byte>`)
- `EvmPooledMemory.cs` — struct-based pooled memory
- `GasPolicy/IGasPolicy.cs` — generic gas policy (static abstract, struct constraint)
- `TransactionProcessing/TransactionProcessor.cs` — transaction execution

Rules: no LINQ, no exceptions in hot paths, use `Span<byte>`/`stackalloc`, pass gas as `ref TGasPolicy`, use `ValueHash256` not `Hash256`, use `KeccakCache`.
