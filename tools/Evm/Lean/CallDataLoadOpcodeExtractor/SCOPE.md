# Operational CALLDATALOAD extractor scope

This package pins the standard-mainnet Amsterdam production route for exactly
`CALLDATALOAD` (`0x35`). Its unconditional opcode-table assignment is closed
over the no-trace, no-trace-cancelable, traced, and traced-cancelable tables for
four concrete `VirtualMachine<EthereumGasPolicy>.ExecuteOpcode` roots. The
standard/zk source selector, `EvmWord` build alias, production VM construction,
complete fork ancestry, dispatch, handler, calldata provider, gas policy,
unsafe stack helpers, and tracer interface are fail-closed full-source inputs.
The concrete `EthereumVirtualMachine : VirtualMachine<EthereumGasPolicy>` type,
the non-generic `IVirtualMachine : IVirtualMachine<EthereumGasPolicy>` alias,
and `BlockProcessingModule.Load` scoped `IVirtualMachine` registration are
separately exact-admitted and validated, closing the standard production entry
to those generic roots.
Selected declarations and methods additionally have exact namespace, nested
owner, generic arity, parameter signature, syntax kind, and canonical syntax
admissions.

The typed IR and two independent executable Lean transitions cover instruction
trace start before dispatch-local PC and opcode-count increments; the fixed
VeryLow charge before checked stack depth; non-mutating out-of-gas and
underflow stack residue; charged underflow; the full-word `PopUInt256` offset
semantics realized by the optimized `ReadMemoryPositionFromSlot` low-limb plus
high-limb rejection; the bottom-first instruction-start stack payload obtained
by reversing the model's top-first words; a 32-byte calldata read with
right-zero-padding; in-place
top-slot replacement; the one-byte zero trace payload for inaccessible offsets;
the raw 32-byte result payload for in-range offsets; post-charge successful
trace closure; and the post-opcode cancellation boundary. Outer closure clears
execution gas on OOG, reports remaining gas before error, and normalizes the
advanced dispatch-local fault PC to the original frame PC. Executable vectors
cover offset zero, a one-byte tail, exact end, beyond end, `2^64`, maximum
`UInt256`, one-short OOG, underflow, traced, and cancelable tables.
The traced vector uses two asymmetric words and proves their bottom-first
payload order.
The production correspondence is bounded to calldata whose length is at most
`Int32.MaxValue`, gas at most `UInt64.MaxValue`, and PC/opcode-count values below
`Int32.MaxValue`. These match the production `ReadOnlyMemory<byte>`, gas, and
counter representations and make the `uint` length conversion and increments
exact.

Generated Lean is theorem-free and emitted from round-tripped semantic IR. It
imports the independent specification for representation types only and does
not call or alias the handwritten transition. Refinement proves helper,
transition, Amsterdam, outer-closure, and fault-PC equality, then states
order/residue/padding/payload boundary theorems directly over the generated
transition.

This is not a proof of .NET compilation, CLR/JIT/AOT execution, function-pointer
tail calls, cancellation scheduling, or hardware behavior. The proof assumes
the external `UInt256.IsUint64`/`u0` contract and the admitted unsafe
`EvmWord`/stack, native-endian conversion, unaligned access, vector-lane, and
`Span` operations implement the modeled big-endian word. The admitted
`VmState.MemoryStacks` and `TraceStack.ToRawBytes` paths establish physical
bottom-first word order and copying; decoding each raw slot into a Lean word
remains under that representation premise. Tracing callbacks are assumed total
and non-throwing. Capability-dependent start payloads for memory, bottom-first
stack, and return data are modeled in exact order; the production environment
object is abstracted. Transaction rollback, persistence, state-gas settlement,
alternate gas policies, and zkEVM execution remain outside this package.
