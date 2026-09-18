# Memory, stack, and control-flow reference slice

`MemoryStackControl.lean` is an executable, handwritten Lean reference for a
bounded Amsterdam EVM instruction slice: stack families through depth 16,
byte-addressed zero-extended memory, code/calldata/returndata copying, and
ordinary control flow and halts. `MemoryStackControlVectors.lean` provides 36
literal boundary snapshots and mutation checks.

State memory is a `Memory` wrapper whose allocation length carries a proof of
32-byte alignment. Test input is canonicalized with zero extension, so no
state reachable through this reference can represent an unaligned allocation;
the `MSIZE` regression records that behavior. Each state constructor and
`step`/`run` has an alignment theorem.

RETURN and REVERT retain the stack tail left after their two operands are
popped. Invalid JUMP and taken invalid JUMPI, and an out-of-bounds
RETURNDATACOPY, retain their post-pop tail and their pre-instruction PC. A
zero-size returndata copy at `source = returndata.length` succeeds, while one
past that boundary faults. Reaching or passing the end of code halts with PC
unchanged: it equals `code.length` at an exact boundary and retains any prior
overshoot, such as a truncated `PUSH2` ending at PC 3 for two bytes of code.

For RETURN and REVERT this model records Nethermind's internal post-dispatch
PC (`opcodePc + 1`). `VirtualMachine.Dispatch.cs` advances `pc` before the
handler, and `VirtualMachine.cs` persists that counter on normal halts; the
Lean `eelsHaltPc` relation maps it back to EELS's unchanged opcode offset.
This is a representation convention only, not a production refinement proof.

The state carries `gasLeft` only so `GAS` can observe it. It intentionally does
not charge gas or prove out-of-gas behavior. `RequiredEvmGasScheduleExtension`
names the still-needed base/very-low/low/mid/high, JUMPDEST, copy-word, and
linear/quadratic-memory schedule fields without adding another concrete
schedule instance.

This is not a production extraction or refinement proof. It does not model
dynamic gas, frames, calls, world state, storage, logs, precompiles, code
validation, host allocation limits, or unmodeled opcode semantics. The
`unmodeledOpcode` result is deliberately distinct from literal EVM `INVALID`.
