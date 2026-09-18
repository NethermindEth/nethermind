# EVM frame-machine extraction scope

Status: Stage A routing and dependency extraction is independently accepted. Frame execution, settlement, precompile bodies, transaction installation, and whole-EVM refinement remain fail-closed.

## Target boundary

The eventual closed production root is the inherited implementation reached by `EthereumVirtualMachine`:

```text
EthereumVirtualMachine
  -> VirtualMachine<EthereumGasPolicy>
  -> ExecuteTransaction<TTracingInst>(VmState<EthereumGasPolicy>, IWorldState, ITxTracer)
```

The fork is the standard Nethermind build at `Nethermind.Specs.Forks.Amsterdam`. The profile keeps the four production dispatch specializations distinct:

- no tracing, no cancellation;
- no tracing, cancellation enabled;
- tracing, no cancellation;
- tracing, cancellation enabled.

The profile and schema bind the exact generic method identity and the closed `EthereumGasPolicy`; arbitrary non-empty replacements are rejected. The closed model vocabulary is `fresh`, `continuation`, `running` for phases; `success`, `revert`, `exception` for exits; and `ordinary`, `handleException`, `handleFailure`, `nestedPrecompileSoftFailure`, `directPrecompileSoftFailure` for control routes. Invalid-code and out-of-gas code deposit are internal branches of successful child CREATE settlement, not synthetic frame exits. Any vocabulary change requires a source-derived profile and schema revision.

## Intended refinement claim

After every gate below is closed, the generated frame driver should have the same observable result as the independent Lean frame-machine reference for every admitted initial frame and finite execution:

- dispatch-table-specific route selection for all four modes and every value from `0x00` through `0xff` (1024 closed roots);
- enabled, disabled, and bad-instruction dispatch;
- exact continuation, child suspension, LIFO parent restoration, and resumption;
- success, explicit revert, and exception settlement, including internal CREATE code-deposit failures and distinct direct/nested precompile failure control;
- execution gas, state reservoir, spill, state-used, and refund-counter merge;
- world/access/log/destroy/returndata effects represented at the adapter boundary;
- mainnet Amsterdam precompile address and activation routing;
- top-level result classification and trace closure;
- a proof-carrying completed-result projection toward `TransactionReference.Oracle.executeEvmCall` without replacing lifecycle-owned transaction state, with the RIPEMD dirty-touch latch retained for the outer rollback consumer.

The opcode bodies and cryptographic precompile bodies are composition dependencies. This package must bind their accepted manifests and theorems; it must not reimplement or assume their result silently.

## Closed Stage A boundary

- `EvmFrameMachineProfile` byte-pins 114 complete production/raw inputs, resolves and syntax-pins 181 exact Roslyn selectors, and binds 14 opcode-package artifact triples plus 18 explicitly unadmitted precompile-wrapper identities. All 14 packages carry 16 adequate operational-refinement theorem identities.
- All four production handler modes and all 256 bytes are represented as 1024 table-major routes: 612 Amsterdam-enabled roots and 412 explicit bad-instruction routes (103 per table). The four explicit `INVALID` routes retain their production `InvalidOpcode` roots; the remaining 408 use the table-specific default `BadInstructionOpcode` root. No Amsterdam-disabled route remains. The only sibling overlap, SLOTNUM, is explicitly assigned to `ControlFlowOpcodeExtractor`.
- The adequately proved sibling packages cover all 153 unique Amsterdam bytes and 612 enabled table-byte routes. Environment contributes and operationally admits 19 unique bytes/76 routes after the SLOTNUM overlap. CallCreate contributes and operationally admits 7 unique bytes/28 routes through its independently accepted package-wide theorem over all four tables, operations, production-domain states, oracles, and child outcomes. The independently accepted `CallDataLoadOpcodeExtractor` owns and operationally admits all four CALLDATALOAD routes. The metadata-only `MemoryControlOpcodeExtractor` is not misrepresented as an operational theorem.
- `EvmFrameMachineLeanEmitter` emits deterministic theorem-free route, precompile-identity, and dependency lookup data only. It contains no executable frame transition or copied handwritten transition. Its generated module imports and `#check`s the exact fully qualified theorem identity of every admitted sibling; the pinned proof-module bytes and normalized theorem-signature digest are part of the dependency boundary.
- `Specification/StageARouting.lean` defines independent linear route and precompile lookup. `Refinement/StageARouting.lean` proves bounded equality for all 1024 table/byte pairs and all addresses `0..256`, plus exact route, precompile, and dependency cardinalities.

## Later-stage scaffold

- `FrameMachineExecution.lean` is an independent executable reference whose lookup is keyed by `DispatchTable × Byte` and whose child settlement selects an explicit operation sequence from `ExecutionType`, create/new-account charge flags, snapshot, physical preexistence, refund advancement, and failure-control origin. Refund/restore/deposit branches are composed from individual source-bound gas, journal, trace, code-deposit, and account-deletion leaves instead of one opaque merge callback.
- `EvmFrameMachine.lean` defines parameterized equality relations for extracted routes, callback leaves, settlement primitives, manifests, and driver definitions; it contains no caller-filled proposition bundle and supplies no proof.
- `TransactionAdapter.lean` accepts only a source-projected top-level message-call/create machine with production-zero initial `VmState.Refund`, an actually completed fuel-bounded run, valid control route, nonnegative final `VmState.Refund`, and equality between that refund and the opcode gas counter. It carries the RIPEMD touch latch into an explicit post-rollback world transition and exposes no installable oracle while the shared transaction state lacks that field. It cannot classify by echoing `Input.execution`; the latter appears only as a proof obligation.

## Explicit premises and exclusions

These are not hidden inside the eventual frame theorem:

- C# compilation, CLR/JIT/AOT correctness, function-pointer tail calls, unsafe stack layout, pooling, SIMD, and hardware behavior;
- concrete trie/database persistence, code-cache coherence, journal-storage implementation, and tracer implementation correctness;
- native and cryptographic precompile algorithms until separately wrapped and refined;
- composition of the accepted opcode-package theorems into the still-absent executable frame transition;
- transaction validation, fee purchase/refund, receipt construction, state-root computation, commit/reset, block accounting, block production, RPC, and concurrency, except that the exact outer-rollback RIPEMD latch-consumption branch is retained as an adapter boundary;
- liveness of the fuel-bounded Lean driver until a gas-progress/fuel-adequacy result is proved.

Consequently, even a completed package would refine the production EVM frame boundary, not the whole Nethermind client.

## Remaining acceptance gates

1. Admit production wrappers and bodies for all 18 precompiles; Stage A pins their identities and addresses but deliberately records `admitted = false`.
2. Derive a theorem-free executable frame transition from source-admitted semantic IR and bind every settlement primitive, world/journal adapter, tracer adapter, and opcode/precompile callback.
3. Prove generated/reference equality for frame step, each child-exit settlement, dispatch iteration, failure-origin ordering, and the closed Amsterdam entrypoint.
4. Prove the generated production-input projector and completed transaction adapter, including `VmState.Refund`, adequate fuel, lifecycle control, and post-rollback RIPEMD latch consumption.
5. Separately review every later execution/refinement stage before integration; the Stage A review is complete.

`acceptanceState = stage-a-admitted` means only the bounded routing/dependency layer is emitted and proved. It must never be read as a whole-frame or whole-Nethermind verification claim.
