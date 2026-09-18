# Memory, stack, and control opcode extraction scope

This subtree extracts a bounded Amsterdam production surface for 84 opcodes:
`STOP`, `POP`, `PUSH0` through `PUSH32`, `DUP1` through `DUP16`, `SWAP1`
through `SWAP16`, `MLOAD`, `MSTORE`, `MSTORE8`, `CALLDATALOAD`,
`CALLDATACOPY`, `CODECOPY`, `RETURNDATACOPY`, `JUMP`, `JUMPI`, `PC`,
`MSIZE`, `GAS`, `JUMPDEST`, `MCOPY`, `RETURN`, `REVERT`, and `INVALID`.

## Admitted production boundary

The extractor admits the complete token sequence of every selected type,
field, property, and method under an owner-, resource-, namespace-, and
source-path-qualified identity; duplicate identities are rejected. The 167 admissions cover the opcode handlers and
their semantic instruction methods, stack-width markers, gas-policy operations,
memory-expansion operations, jump-destination lookup, opcode byte enum, dispatch
loop, all four standard dispatch tables, the concrete Ethereum virtual machine,
its standard opcode-table cache/refresh path, mainnet block-processing
registration, named-fork root-to-leaf construction, and the
Olympic-to-Amsterdam release-spec lineage. A separate exact raw SHA-256 admission
pins `src/Nethermind/Directory.Build.targets`, including its mutually exclusive
standard-versus-zkEVM source selection.

Extraction rejects an unadmitted token change, a selected member that is absent
or ambiguous, a duplicate opcode byte, a noncanonical dispatch target or type
argument, a later or unreachable competing dispatch assignment, a reachable
early return, a dead semantic branch, a using alias, and every unreviewed
preprocessor directive. The few directives already present in selected source
files are accepted only as their exact reviewed sequences. The checked artifact
contains 336 closed roots: 84 opcodes in each of `NoTrace`,
`NoTraceCancelable`, `Traced`, and `TracedCancelable`. Nested handler type
parameters are closed to each table's tracing flag and to `EthereumGasPolicy`;
deserialized-IR validation uses case-sensitive required-constructor binding and
rejects malformed JSON, unmapped members, missing fields, nested null values,
any changed header/root/fork/gas/descriptor/lineage/obligation field,
and any remaining placeholder, wrong root, missing key, or duplicate
opcode/table key.

The generated Lean module is emitted only after serializing and deserializing
the semantic IR. It contains definitions and source identities, but no theorem,
axiom, or import of the handwritten model. The refinement module proves that
the 84 source-derived descriptors have unique exact bytes and agree with the
companion specification on decoding, fixed/dynamic gas class, numeric fixed gas,
copy-word gas, memory-linear gas, memory access, minimum stack depth and net
growth, exit mode, activation, Amsterdam availability, and the exact source fork
lineage. Separate decidable theorems prove every specialization's table flags and
fully closed root, and prove that its opcode/table keys are exactly the unique
84-by-4 Cartesian product.

## Companion model and composition gap

`Eip803x.Evm.MemoryStackControl` models stack, memory, and control results but
deliberately omits opcode gas charging and exact production provider behavior.
`Specification/MemoryControlExecution.lean` therefore supplies a companion
charge plan with configurable fixed, copy-word, and memory-expansion costs. It
models zero-length access, 32-byte and byte accesses, copy destinations,
`MCOPY`'s largest source/destination start, and return/revert ranges using the
shared `MemoryGas` model.

The proved refinement is metadata refinement, not an end-to-end operational
equivalence proof for each C# handler. The complete C# bodies are fail-closed
admissions, but Roslyn syntax has not been translated into an executable Lean
operational semantics. In particular, this subtree does not prove the order of
stack mutation versus failure, returndata bounds, jump validity, byte copying,
or observable tracing effects. Those require a subsequent translation of the
admitted method bodies and a composition theorem with `MemoryStackControl`,
`MemoryGas`, and the common gas/frame machine.

## Explicit assumptions and open obligations

- The C# compiler, CLR/JIT, generic specialization, unsafe code, and function
  pointer invocation implement the admitted C# token stream as specified.
- `EvmStack` implements 256-bit big-endian words, enforces the 1024-item limit,
  and gives the admitted `Pop`, `Push`, `Dup`, and `Swap` operations their
  intended stack semantics without aliasing or unsafe-memory faults.
- `EvmPooledMemory` is zero-initialized on expansion, implements the admitted
  reads/writes/copies faithfully, and gives `MCOPY` memmove behavior for
  overlapping ranges.
- `UInt256` conversions, checked range guards, host `int`/`ulong` widths, and
  arithmetic in the gas policy agree with the Lean natural-number model. The
  production maximum memory range remains `int.MaxValue - 31`.
- `JumpDestinationAnalyzer` has been populated from the same bytecode and its
  bitmap recognizes exactly instruction-aligned `JUMPDEST` bytes, excluding
  bytes inside push immediates.
- The untraced truncated-`PUSH` fast path is observationally equivalent to
  zero-padding its missing immediate bytes. Trace and cancellation branches do
  not alter consensus-visible stack, memory, program-counter, gas, or exit data.
- The admitted mainnet registration remains the effective `IVirtualMachine`
  implementation, and no plugin or alternate container registration replaces
  it for the execution being claimed.
- Frame settlement maps `STOP`, `RETURN`, `REVERT`, and exceptional `INVALID`
  exits to the shared frame machine correctly; it is outside this subtree.

No production Nethermind defect was established while constructing this
boundary.
