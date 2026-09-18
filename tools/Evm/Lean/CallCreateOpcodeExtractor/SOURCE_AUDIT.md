# CALL/CREATE operational source-closure audit

Status: the corrected SELFDESTRUCT normalization and independently owned
generated/reference semantics pass package-local warning-as-error, mutation,
Lean, formatting, and deterministic-regeneration gates. Independent review is
pending. The closure contains 79 C# sources, 4 raw inputs, and 105 exact syntax
admissions.

## Closed production roots

The intended production root is
`EthereumVirtualMachine : IVirtualMachine`, closed to
`VirtualMachine<EthereumGasPolicy>` by `BlockProcessingModule` for the standard
mainnet build. `Directory.Build.targets` is therefore a raw build input: the
profile rejects the zkEVM `*.zkevm.cs` selection and admits the standard
`EvmInstructions.Call.std.cs` implementation. `Directory.Build.props` is a
second raw build input because its `EvmWord` alias fixes the stack word type.
The inert shared representation module and the independently authored
operational reference are the remaining two raw inputs. The emitted kernel may
import only data/result vocabularies from the representation module. Dispatch
flags, address normalization, deposit ordering, and all other executable
projections are independently implemented in the generated and reference
modules. The emitter is rejected if it imports, names, or calls the operational
reference.

Amsterdam is closed through `NamedReleaseSpec` and every named predecessor.
The resulting feature specialization is:

- EIP-150, EIP-158, EIP-2929, EIP-3860, EIP-6780, EIP-7702, EIP-7708,
  EIP-8037, EIP-8038, and EIP-8246 enabled;
- four dispatch tables: untraced/traced crossed with
  non-cancelable/cancelable;
- four CALL-family opcodes, two CREATE-family opcodes, and SELFDESTRUCT,
  yielding 28 exact closed opcode roots.

The profile must admit both the selector chain and its Amsterdam leaf. Merely
admitting the leaf generic instantiation would miss changes in the fork flag
selection that make it reachable.

## Required syntax closure

The admission is exact by relative path, case-sensitive namespace, nested
owner path and owner generic arity, declaration kind, member name, member
generic arity, parameter count and normalized parameter types, Roslyn
`SyntaxKind`, complete-node syntax digest, complete-file byte digest, and
complete-file canonical Roslyn digest. The following groups are required.

### Wiring, fork, and dispatch

- `Nethermind.Init/Modules/BlockProcessingModule.cs`: the production VM
  registration.
- `Nethermind.Specs/Forks/NamedReleaseSpec.cs` and the complete named fork
  ancestry through `25_Amsterdam.cs`.
- `Nethermind.Evm/Instruction.cs`, `TypeFlags.cs`, and
  `DispatchFlags.std.cs`: opcode identity and the four tracing/cancellation
  table identities.
- `VirtualMachine.OpcodeHandlers.cs`: opcode-table preparation, every
  CALL/CREATE/SELFDESTRUCT selector level, `IOpcodeBody`, and the three wrapper
  bodies.
- `VirtualMachine.Dispatch.cs`: `ExecuteOpcode`, checked exit, terminating and
  continuable handler construction, and the dispatch loop. This includes the
  CREATE-specific instruction-trace ownership marker.
- `VirtualMachine.cs`: transaction loop, child-frame suspend/enter,
  success/REVERT/exception merge, code deposit, result push and output copy,
  `RunByteCode`, state-gas refund propagation, transfer/selfdestruct logs, and
  instruction/action tracing boundaries. The exact
  `CanExecutePrecompileCallDirectly` and `TryRunPrecompileDirectly` members pin
  the RIPEMD160 exclusion and direct execution oracle crossing.
- `VirtualMachine.ExecutionHandlers.cs` and the standard execution-handler
  selection: frame initialization, value-transfer logging, and the state-gas
  credit indirection.

### Handler bodies and immediate adapters

- `EvmInstructions.Call.cs` and `EvmInstructions.Call.std.cs`:
  `InstructionCall`, `CreateFullCallFrame`, and the standard inline STATICCALL
  precompile path.
- `EvmInstructions.Create.cs`: `InstructionCreate` and
  `CompleteCreateWithoutChild`.
- `EvmInstructions.ControlFlow.cs`: `InstructionSelfDestruct` only; unrelated
  control-flow declarations remain competing declarations and full-file input.
- `EvmInstructions.Spec.cs`: `AccessSpec`, `CallSpec`, `CreateSpec`, and
  `SelfDestructSpec`, including all selected fork-flag projections.
- `EvmCalculations.cs`: checked CREATE init-code ceiling division.
- `ContractAddress.cs`: the exact CREATE/CREATE2 address-oracle crossing.
- `CodeAnalysis/CodeInfo.cs`, `CodeInfoFactory.cs`, `ICodeInfoRepository.cs`,
  and the called repository members: delegated code discovery, empty-code and
  precompile routing, init-code analysis, and runtime-code insertion.

### Gas and memory/stack behavior

- `GasCostOf.cs`, `Eip8037Constants.cs`, `Eip8038Constants.cs`,
  `SpecGasCosts.cs`, `IGasCost.cs`, `IGasPolicy.cs`, and
  `EthereumGasPolicy.cs`.
- `AccountAccessKind.cs` and `AccountAccessPricingKernel.cs` for default and
  SELFDESTRUCT-beneficiary warmth/pricing.
- `StateGasChargeKernel.cs`, `StateGasTransitionKernel.cs`, and
  `StateGasTransitionAdapterKernel.cs` for reservoir spill, child merge,
  REVERT restoration, exceptional-halt restoration, code-deposit conversion,
  and LIFO refill.
- `CodeDepositHandler.cs` for checked runtime-code execution/state cost and
  invalid-code classification.
- `EvmStack.cs`, `EvmWordExtensions.cs`, and `EvmPooledMemory.cs` for exact pop
  order, the cached `PopAddress` low-20-byte conversion, zero/success/address
  pushes, memory expansion, owned init-code load, input load, and clipped output
  save.

### Frame, access journal, world-state, and trace boundary

- `VmState.cs`, `VirtualMachine.CallResult.cs`, `ExecutionType.cs`,
  `ExecutionEnvironment.cs`, and `StackAccessTracker.cs`: child construction,
  snapshot markers, access/destroy/log journals, parent continuation, and
  result classification.
- `State/IWorldState.cs` and `State/WorldStateExtensions.cs`: the exact state
  adapter calls used by these handlers. Concrete trie/database implementations
  are not claimed equivalent by the generated transition.
- `Tracing/ITxTracer.cs`, `Tracing/TracerExtensions.cs`, `TraceStack.cs`, and
  `TraceMemory.cs`: instruction start/end/error, pushed values, action frames,
  access reports, transfer/selfdestruct reports, and clipped output reports.

Every item above must appear either as an exact syntax admission or as a named
raw input. A source path without an admitted relevant node is rejected; an
admitted node from an unlisted source is rejected. Selected members must be
unique, and competing case aliases, overloads, partial declarations, generic
owners, and standard/zkEVM implementations are checked explicitly.

## Production order to preserve

The independent `CallCreateOperational` module specifies this complete order
without importing the generated kernel. The package-wide
`closed_amsterdam_refines` theorem quantifies every table, opcode, oracle,
machine state, and child outcome and equates both the immediate transition and
the resume/settlement transition. The existing `CallCreateFrame` and
`SelfDestruct` lemmas remain the independently maintained foundation checks for
frame gas, refill, deposit, and SELFDESTRUCT pricing behavior.

### CALL, CALLCODE, DELEGATECALL, STATICCALL

The transition starts instruction tracing before the one-byte PC and opcode
counter advance. It then pops requested gas and the code-source word, reduces
the code source to its low 160 bits exactly as production `PopAddress` does,
derives or pops value according to call kind, and atomically completes the four memory
operands. Stack-underflow residue is the residue of the production sequential
pops, not an all-or-nothing abstract pop.

Static value rejection occurs before any gas or access mutation. A value-bearing
Amsterdam CALL/CALLCODE then consumes the fixed EIP-2780/EIP-8038 transfer
charge. CALL base, input-memory expansion, and output-memory expansion are
charged in that order. Code-source access warms before code/delegation lookup;
an optional delegated target warms only after discovery. The dead-target
NEW_ACCOUNT state charge follows both accesses. EIP-150 reserves requested
child execution gas, capped to 63/64 of the execution gas that remains after
those stages; the stipend is then added to child gas without a second parent
debit.

Depth or balance failure is post-reservation. It clears return data, pushes
zero, returns reserved execution gas, refills the NEW_ACCOUNT state charge when
one was taken, reports the production error/update events, and continues. The
empty-code fast path pushes success before world-state mutation and returns the
reservation. The standard untraced STATICCALL/precompile inline route is a
separate branch with an explicit precompile oracle. It also requires
`CanExecutePrecompileCallDirectly`; normalized RIPEMD160 address 3, including
high-bit stack aliases such as `2^160 + 3`, is excluded because its full-frame
EIP-161 touch behavior is not replayed inline. Returned oracle gas is
accepted only when no greater than the reserved child gas. All other successful
calls
snapshot, debit transfer value, load the exact zero-extended input, normalize a
zero-length output destination to zero, transfer the state reservoir into the
child gas, stage a child frame, and suspend.

### CREATE and CREATE2

Static rejection precedes stack access. CREATE pops value/offset/length;
CREATE2 then pops the salt. The transition checks EIP-3860 size, checked word
ceiling, fixed/init-word/CREATE2-hash execution charge, and memory expansion in
that order. Depth failure occurs after those charges. Exact owned init-code is
loaded before balance and nonce reads. Balance and nonce failures push zero and
close the instruction without warming a destination.

The address derivation is an explicit oracle. Destination warming precedes the
single collision query that returns physical-leaf and logical-account facts.
NEW_ACCOUNT state gas is charged exactly when the logical account does not
exist, before the CREATE handler records its pre-reservation instruction-trace
end. EIP-150 then reserves all capped remaining execution gas. The creator nonce
is incremented and the snapshot is taken before collision handling. Collision
burns the reserved execution gas, refills any CREATE state charge, clears
return data, and pushes zero without a child. Noncollision debits value, creates
the child environment and transfers the state reservoir into a staged child
frame before suspend.

Successful CREATE intentionally owns the pre-child instruction-trace end. The
generic continuable dispatch and `RunByteCode` suspend closure both skip that
one boundary; parent resume reports the created address/result after the child
settles. Depth/balance/nonce and collision continuations each close exactly
once. This preserves the pre-reservation gas value used by Geth-style gas-cost
tracing.

### Child resume and merge

The generated transition must treat child execution as a supplied, checked
oracle outcome, not as an implementation of arbitrary bytecode. It nevertheless
models the production merge around that oracle:

- success refunds remaining child execution and merges child state gas;
- CALL success retains output, clips it to the requested length, stages a
  success result, commits access/destroy/log journals, then repays spill;
- explicit REVERT returns remaining execution gas, restores state reservoir and
  journals, preserves revert output for the caller, and refills an upfront
  NEW_ACCOUNT/CREATE state charge;
- exceptional halt restores journals and the non-durable state-gas component,
  burns child execution, clears output, and refills the applicable upfront state
  charge;
- CREATE success refunds the child into the parent, evaluates and charges
  checked code-deposit execution/state gas on the parent, commits the child,
  then repays spill; invalid code or mandatory deposit OOG converts the
  already-refunded child to halt semantics, restores the snapshot, deletes a
  newly materialized destination when required, returns zero, and refills the
  CREATE state charge;
- a successful CREATE stages the created address, while REVERT/exception/deposit
  failure stages zero; the parent continuation pushes it before the resumed
  instruction stream and then emits the post-child remaining-gas report.

### SELFDESTRUCT

Static rejection precedes all charges. Amsterdam consumes the legacy 5,000
execution charge before popping the beneficiary. The popped word is reduced to
its low 160 bits immediately, matching production `PopAddress`, before the
beneficiary access warms and charges or any later beneficiary-dependent effect
is observed. In particular, `2^160 + 3` is address `3`. The
created-in-this-transaction fact controls the destroy-list
entry under EIP-6780. The balance and action trace are read before recipient
existence/deadness classification. For a positive transfer to a dead account,
EIP-8038 charges ACCOUNT_WRITE execution gas before NEW_ACCOUNT state gas.
Only after both succeed does the transition create/credit the beneficiary,
apply the EIP-8246/EIP-6780 self-target exception, append the selfdestruct log,
and debit the source. The terminating handler and outer VM own the terminal
instruction-trace closure; the immediate body does not fabricate one.

## Explicit premises and non-claims

The generated transition uses explicit inputs for state reads, access-list
initial facts, delegation discovery, precompile execution, CREATE/CREATE2
address derivation and hashing, runtime-code validity, child execution,
and journal/world tokens. Refinement requires consistency premises connecting
those inputs to the admitted adapter call sites.

No theorem claims equivalence for Keccak, RLP, address/hash representation,
precompile implementations, arbitrary child bytecode, trie/database state,
journal storage internals, code cache behavior, pooled allocation, unsafe/SIMD
layout, CLR/JIT/AOT, native libraries, tracer callback implementation or
exceptions, cancellation delivery, metrics, transaction/block composition,
or hardware. The source admission establishes only the bounded ordering and
projection around those named boundaries.

Trace theorems establish event kind, order, and ownership only. Capability-
dependent stack/memory/return-data payloads and action value/from/to/input,
execution-type, and precompile payloads remain explicit adapter premises.
