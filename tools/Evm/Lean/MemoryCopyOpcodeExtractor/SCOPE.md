# Operational memory/copy/GAS extractor scope

This package pins the standard-mainnet Amsterdam production route for exactly
`MLOAD`, `MSTORE`, `MSTORE8`, `MSIZE`, `CALLDATACOPY`, `CODECOPY`,
`RETURNDATACOPY`, `MCOPY`, and `GAS`. Four instruction-tracing/cancellation
dispatch tables close each opcode over `VirtualMachine<EthereumGasPolicy>`, for
exactly 36 closed generic roots. `RETURNDATACOPY` is tied to its EIP-211 fork
gate and `MCOPY` to EIP-5656. The standard build selector, `EvmWord` build alias, Ethereum VM DI root,
all fork ancestors through the synthetic Amsterdam profile, opcode tables,
handlers, stack, memory, gas policy, tracing interface, and constants are in a
fail-closed full-source and exact-syntax manifest. The operand-decoding closure
includes the standard `EvmStack` partial, `Bytes.Bswap64`, and the accelerated
`EvmWordExtensions.ByteSwap` route used by the copy opcodes.

The independent Lean specification includes the production-visible order of
dispatch trace start, PC increment, opcode counter increment, stack mutation,
fixed/dynamic charge, checked word count, logical memory-size installation,
expansion debit, access violation, data movement, stack/memory callbacks, and
successful trace closure. It distinguishes immediate handler residue from the
outer `RunByteCode` failure closure. In particular, copy operands are removed
before dynamic charge, an oversized word count first attempts the three-gas
base charge, a failed expansion debit retains the newly installed logical size,
`RETURNDATACOPY` checks its source end before destination expansion, zero-length
copies touch no memory, `MCOPY` is overlap-safe, and `GAS` pushes post-base-charge
execution gas while leaving every EIP-8037 state-gas field unchanged. Memory
bytes and memory-size callbacks are distinct events; `MLOAD` reports the full
32-byte pushed word, while `MSIZE` and `GAS` report their production 8-byte push
payloads. The closed Amsterdam theorem also covers outer out-of-gas clearing,
finish-before-error callback order, and post-dispatch fault-PC normalization.

The generated Lean contains its own executable transition emitted from typed,
source-admitted semantic IR. The operational specification is pinned as a
proof target and supplies shared representation types, but none of its
executable definitions are read, copied, aliased, or invoked by the emitter.
Refinement first proves the generated interpreter equals the independent operational specification, then projects successful
steps to the existing gas-erased `MemoryStackControl` behavior. The projection
normalizes `GAS` to its post-charge observation and deliberately omits execution
gas and trace fields absent from the older model.

This is not whole-loop, CLR, unsafe-runtime, allocator, or tracer verification.
The proof assumes the admitted `UInt256`/unsafe-stack representation matches the
Lean word/stack view; `EvmPooledMemory` pooling, zero initialization, bounds,
aliasing, and `Span.CopyTo` implement the modeled byte memory and memmove;
the CLR intrinsic, `BinaryPrimitives`, and `Unsafe` endian operations implement
their admitted byte-level contracts;
tracing callbacks do not throw; and JIT specialization/function-pointer dispatch
executes the admitted closed roots. The source trace callback on `MLOAD` is the
intentional Parity-compatible behavior and is not classified as a production
bug. Frame settlement, rollback, cancellation, transaction/block composition,
persistence, alternate gas policies, and zkEVM execution remain outside this
package.
