# Amsterdam control-flow opcode extraction scope

This package is a bounded, package-only refinement for the standard-build
`EthereumVirtualMachine` under Amsterdam. It covers `STOP` (`0x00`), `SLOTNUM`
(`0x4b`), `JUMP` (`0x56`), `JUMPI` (`0x57`), `PC` (`0x58`), `JUMPDEST`
(`0x5b`), `RETURN` (`0xf3`), and `REVERT` (`0xfd`). The extractor closes all
32 enabled opcode/table roots across no-trace, no-trace-cancelable, traced, and
traced-cancelable dispatch. The gated `SLOTNUM` and `REVERT` entries additionally
close their eight table assignments onto the four shared terminating
`BadInstructionOpcode` roots.

The source boundary is 57 case-sensitive, byte-pinned C# files, the raw standard
build selector and `EvmWord` alias inputs, and the theorem-free operational reference. It admits 206 exact
Roslyn declarations qualified by source, namespace, owner name and generic
arity, member kind and name, method generic arity, parameter count and modifier-
sensitive parameter types, syntax kind, and complete token hash. The manifest
also binds the complete canonical IR and independently emitted Lean bytes to
recomputed lowercase SHA-256 digests. The generated kernel is deterministic and
contains definitions only.

The operational claim covers:

- production handler selection and both fork gates;
- instruction-trace start before dispatcher PC/opcode-count increment;
- false trace ownership for all eight covered descriptors, the inherited
  default-false `IOpcodeBody.EndsInstructionTrace` contract and generic-dispatch
  close gate for their generic bodies, and JUMPI's separate dedicated close;
- distinct memory, memory-size, stack, returndata, push, remaining-gas, and error
  callback projections in production order, including the exact four-byte `PC`
  and eight-byte `SLOTNUM` stack-push payloads;
- fixed execution-gas debit and execution-gas exhaustion on out-of-gas closure,
  while preserving the non-execution gas fields;
- exact dispatcher-local stack underflow, stack overflow, missing-slot,
  invalid-jump, and invalid-memory-range residue;
- independently emitted instruction-boundary jump validation derived from the
  admitted `STOP`, `JUMPDEST`, `PUSH1`, and `PUSH32` source facts, excluding
  bytes inside PUSH immediates and signed-int-unaddressable destinations;
- untraced taken-jump fusion with the landed `JUMPDEST` PC advance, opcode count,
  and one-gas charge;
- zero-condition `JUMPI` fallthrough without destination validation;
- expansion-gas affordability before logical memory growth, zero-length behavior,
  owned staged output, and the distinct STOP/RETURN/REVERT termination results.

The generated kernel deliberately reuses the disclosed `wordToBytes`, bounded
stack push/pop, byte-memory expansion/read, and execution-gas debit definitions
as foundational representation premises. Its jump-destination scanner is not
shared with or delegated to the handwritten `MemoryStackControl` transition.

The trace model is an explicit semantic projection. `start` retains opcode, PC,
and gas; the optional memory, memory-size, bottom-first logical stack, and
prior-returndata callbacks remain separate events; push retains the exact
big-endian byte payload; and finish/error retain gas and failure class. Concrete `ExecutionEnvironment`, `TraceMemory`,
`TraceStack`, integer carrier widths, span lifetimes, and tracer adapter object
representations are open implementation obligations rather than collapsed exact
payload claims.

The handwritten `Eip803x.Evm.MemoryStackControl` model has no gas schedule,
tracing surface, opcode counter, or `SLOTNUM`. The refinement therefore proves
the complete generated transition against the independent operational reference,
plus the unconditional `STOP` code/memory/stack/PC projection to the existing
handwritten model. The package does not claim a general projection of the other
seven operations into that older, gas-free state machine. Gas, tracing, staged
returndata, opcode-count effects, and `SLOTNUM` are proved only in the operational
layer and are not attributed to that older model.

The package gate discovers 11 Microsoft.Testing.Platform tests. Its exhaustive
JSON tests execute 15,109 mutation assertions: 2,150 over the canonical IR and
12,959 over the source manifest. The standalone Lean target builds in 9 jobs;
the generated kernel, operational reference, and refinement also compile
directly with warnings treated as errors.

This package does **not** verify the complete dispatch loop, a complete call
frame, frame rollback, caller returndata installation, transaction or block
settlement, state persistence, the cancellation poll or
`OperationCanceledException`, alternate gas policies, the zkEVM build, Roslyn or
C# compilation correctness, CLR/JIT/AOT behavior, function-pointer tail calls,
unsafe stack memory, pooled-memory allocation/ownership, or hardware execution.
The admitted scalar and SIMD jump-bitmap implementations are assumed
extensionally equal to the independently emitted PUSH-aware scanner on
production-reachable code whose length fits a signed `int`.
The one-handler transition also assumes the admitted production reachability
invariant that staged `ReturnData` is null on handler entry: `RunByteCode` clears
it before dispatch, continuable handlers must not install it, and the covered
`RETURN`/`REVERT` handlers terminate immediately after installing it. All such
boundaries remain explicit assumptions. On exceptional failure, the modeled PC
and stack are the dispatcher-local `programCounter` and `EvmStack` head residue;
`RunByteCode` does not commit those two locals back to `VmState`, and this package
does not claim otherwise.

Primary standards audited for this slice are [EIP-7843](https://eips.ethereum.org/EIPS/eip-7843),
[EIP-140](https://eips.ethereum.org/EIPS/eip-140), the
[Ethereum Yellow Paper](https://ethereum.github.io/yellowpaper/paper.pdf), and
the [execution-specs EVM implementation](https://github.com/ethereum/execution-specs/tree/master/src/ethereum/forks).
