# Pure-word opcode production refinement

This milestone profiles all 26 Amsterdam pure-word opcodes selected by the real
standard Nethermind dispatcher. It does not introduce a C# arithmetic kernel.
The extraction root is each closed specialization of:

```text
VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<
  TOpcode, TTracingInst, TCancelable, OnFlag>
```

For every opcode, the IR names all four standard-build tracing/cancellation
specializations. Its source manifest hashes the production dispatch, opcode
bodies, instruction operations, stack representation, gas policy and tags,
fork gates, concrete virtual-machine type, and mainnet block-processing DI
registration. Extraction rejects changes to the syntax invariants used to form
the profile.

## Artifacts

- `PureWordOpcodeExtractor/Generated/PureWordOpcodeKernel.ir.json` is the
  deterministic 26-opcode, 104-specialization source profile.
- `PureWordOpcodeExtractor/Generated/PureWordOpcodeKernel.source-manifest.json`
  hashes the 40 C# source files plus the raw standard-build source-selection
  file in the accepted closure.
- `Eip803x/Generated/PureWordOpcodeKernel.lean` is a generated, theorem-free
  execution model. It does not import the handwritten pure-stack execution
  model.
- `Eip803x/Refinement/PureWordOpcode.lean` maps the generated types into
  `Eip803x.Evm.PureStackExecution` and proves the single-step structural
  refinement over their intentionally shared `Word`, `Gas`, and bounded-stack
  foundations.

The final theorem, `extracted_execute_refines`, covers opcode identity, the
Amsterdam schedule, program-counter advance, static gas debit and out-of-gas
clearing, stack underflow, all word results, and EXP dynamic byte gas. The
observable result is status plus stack, execution gas, state-gas fields, refund
counter, and program counter, as represented by the shared gas state and this
restricted state projection.

The projection intentionally omits production-dispatch bookkeeping outside
that reference interface, including the opcode counter and trace/cancellation
callbacks.

`PureWordOpcodeExtractor/SCOPE.md` defines the exact fail-closed source
admission boundary used to justify that generated profile.

## Claim boundary

This is a source-profile refinement for the selected direct production roots,
not a proof of the entire EVM or of the CLR. The generated IR records these open
obligations explicitly:

- Roslyn syntax acceptance is not a proof of C# semantics.
- Equivalence between Lean word operations and production `UInt256`, `Int256`,
  byte-order, unsafe stack-slot, and SIMD/scalar implementations remains open.
- JIT specialization, function-pointer dispatch, tracing and cancellation side
  effects, and tail-call behavior remain open.

Memory, storage, calls, creation, logs, halting opcodes, precompiles,
transaction/block processing, persistence, concurrency, and RPC behavior are
outside this milestone. No full-EVM or full-Nethermind verification claim
follows from these artifacts.
