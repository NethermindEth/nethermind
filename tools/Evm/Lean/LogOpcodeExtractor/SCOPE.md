# LOG opcode extraction and refinement scope

This subtree admits a pinned, standard-mainnet production slice for `LOG0`
through `LOG4`, emits theorem-free Lean from a deserialize-after-serialize IR,
and refines its executable single-opcode transition to
`Eip803x.Evm.Logs.execute` and its memory preparation to
`Eip803x.Evm.MemoryGas.prepare` under the assumptions below.

## Admitted production closure

The extractor fails closed over complete Roslyn token sequences for every
selected declaration. Each source-manifest admission key includes the source
path, resource stem, selected owner, and selector; empty or duplicate keys
fail extraction. The readable admission templates cover:

- bytes `0xa0` through `0xa4`, their five unconditional assignments in
  `GenerateOpcodeHandlers`, the `LogOpcode<TOpCount>` wrapper, the ordinary
  function-pointer root, and dispatch's pre-body one-byte PC increment;
- `IOpCount` and `Op0` through `Op4`, plus the complete `InstructionLog` body;
- the atomic offset/length pop, sequential topic pops, standard big-endian
  stack reads, `UInt256` memory-position folding, and the 32-byte `Hash256`
  topic constructor boundary;
- `GasCostOf.Log`, `LogTopic`, `LogData`, and `Memory`; the Ethereum gas-policy
  memory and log charges; logical memory range validation, expansion, payload
  load, and the quadratic `>> 9` term;
- the executing-account, VM-state, access-tracker, `LogEntry`, append-only
  `JournalCollection.Add`, and optional `ITxTracer.ReportLog` boundaries;
- all four live standard dispatch tables (`NoTrace`, `NoTraceCancelable`,
  `Traced`, and `TracedCancelable`), the standard per-spec table cache and
  refresh path, the mainnet `EthereumVirtualMachine` DI registration, and the
  complete Olympic-to-Amsterdam named-fork ancestry.

The raw hash and both opposing `ItemGroup` conditions in
`src/Nethermind/Directory.Build.targets` bind this result to the standard
`.std.cs` sources and exclude `.zkevm.cs`; this is not a zkEVM claim. Using
aliases beyond the two exact, unrelated production aliases in `EvmStack.cs`
and `ExecutionEnvironment.cs`, and unadmitted preprocessor directives are rejected. Competing partial
members, dead or later duplicate dispatch assignments, aliases for selected
opcode bytes, wrong wrapper targets or arguments, and changed source order all
fail extraction.

Some shared admission templates are referenced from the adjacent
`MemoryControlOpcodeExtractor/Admission` directory. They are immutable inputs
to this project and are listed as embedded resources by exact logical name;
LOG-specific templates live here. This avoids maintaining two independently
editable copies of the same reviewed declarations.

## Source-derived IR and generated Lean

The schema records all five descriptors, numeric gas and memory constants, the
17-step source order, and the Cartesian five-by-four dispatch closure. Every
specialization carries its exact `(opcode, table)` key, tracing and cancellation
flags, and a fully closed root such as:

```text
VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<LogOpcode<EvmInstructions.Op2>,OnFlag,OffFlag,OnFlag>
```

The extractor validates the deserialized IR before emitting Lean. It rejects
arbitrary, duplicate, missing, unknown, and unbound roots, as well as reordered
effects or changed bytes, topic counts, stack arity, or PC delta. Generated Lean
contains definitions only and does not import `Eip803x.Evm.Logs`. Its transition
uses every semantic descriptor field: effect order and header arity guard the
step, topic count selects the sequential pops and emission price, and the
source-derived PC delta advances the dispatcher counter.

The separate refinement proves:

- exact, unique opcode bytes and exact five-by-four specialization keys/roots;
- exact descriptor order, arity, topic counts, and Amsterdam gas schedule;
- equivalence of the generated memory cost, range validation, and expansion
  preparation to `Eip803x.Evm.MemoryGas`;
- equivalence of the generated logical LOG transition to the handwritten
  `Logs.execute` transition after erasing the generated PC and tracer-report
  observations;
- one-byte PC advancement for every success and handler-level failure; and
- on success, append preservation for the separately modeled journal and
  optional log-report observations. The generated transition encodes journal
  append before conditional report; the production ordering itself is a
  source-admitted structural boundary.

The transition includes static rejection, atomic header underflow, memory
range rejection, expansion-before-charge behavior, expansion OOG, emission
OOG before topics, partial sequential topic pops on underflow, exact memory
payload, executing address, topic order, journal append, optional log report,
and post-dispatch PC.

## Explicit assumptions and exclusions

This is a restricted structural refinement, not proof that Roslyn syntax is
the C# or CLR semantics. The following remain obligations:

- C# compilation, generic specialization, JIT/function-pointer/tail-call
  behavior, cancellation polling, and opcode counters preserve the admitted
  source behavior;
- unsafe stack slots, SIMD/scalar byte reversal, Nethermind `UInt256`,
  `Address`, and `Hash256` implement the shared Lean word/byte values;
- `EvmPooledMemory` backing buffers, inline memory, array pooling, zero
  initialization, allocation success, ownership, and aliasing implement the
  modeled logical aligned byte list;
- `JournalCollection` and tracer callbacks do not throw and their provider
  internals implement the modeled append observations;
- frame snapshots and rollback, exceptional-frame gas settlement, transaction
  receipt conversion, receipt/Bloom construction, persistence, and reorgs are
  outside this single-opcode handler result.

Instruction-trace start/end payloads are admitted at the dispatcher boundary
but erased by this refinement. Log reports are modeled independently of the
instruction-tracing table because production gates them on
`TxTracer.IsTracingLogs`, not `TTracingInst`. No CLR, allocation, provider,
frame, receipt, Bloom, database, or whole-EVM correctness claim follows from
this slice alone.
