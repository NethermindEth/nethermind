# Refinement architecture

## Design rule

The project does not translate arbitrary Nethermind C# into Lean. Production consensus decisions are progressively isolated into small, pure kernels accepted by a restricted Roslyn extractor. Handwritten Lean specifications are proved equal or related to the generated definitions, while thin production adapters gather facts and apply explicit effects.

```text
Lean specification
      ^ handwritten refinement theorem
Generated Lean definition <- canonical IR <- Roslyn semantic model <- restricted C# kernel
      ^ composition/simulation relation
Production adapter -> EVM -> transaction processor -> block processor
```

The optimized VM may retain structs, spans, pooling, unsafe function-pointer dispatch, and generic flag specialization. Those mechanisms are outside the initial extractable subset and must be connected by adapter laws, reachability checks, and differential execution. Moving a decision to a shadow implementation that production does not call provides test evidence only, not production refinement.

## Restricted production kernels

An extractable kernel has immutable value inputs and an explicit value result or error. It has no database, logging, tracing callback, exception control flow, inheritance, virtual/interface dispatch, reflection, async code, unsafe code, mutable static state, aliasing `ref` values, or uncontrolled allocation. Calls must form a closed manifest-selected graph.

Initial types and operations are:

- booleans, enums, products, result sums, and fixed-width signed/unsigned integers;
- construction, projection, local values, conditionals, switches, and closed static calls;
- arithmetic with explicit checked, wrapping, signed, division, shift, and conversion semantics.

Unsupported Roslyn operations are errors. Machine integers are extracted as bit vectors; refinement to the `Nat`-based specification requires proved range and representation invariants. Fork behavior enters through an immutable `ForkConfig`/`GasSchedule`, not a virtual `IReleaseSpec` read inside a kernel.

For stateful operations, adapters provide immutable facts such as account existence, emptiness, warmth, original/current storage, balances, depth, static context, and code metadata. Kernels return ordered effects such as charges, refunds, warming, writes, logs, creates, deletes, and frame transitions. Applying those effects and journal rollback is a separate simulation obligation.

## Extractor and artifacts

The standalone extractor uses Roslyn `IOperation` and control-flow graphs from a compilation matching the production project. MSBuild supplies source/reference lists, language version, nullable and overflow settings, target platform, and preprocessor symbols. A source manifest selects roots by fully-qualified signature.

Each run produces deterministic, reviewable artifacts:

| Artifact | Purpose |
| --- | --- |
| `extraction.toml` | Pinned roots, source files, allowed intrinsics, closed generic specializations, and fork instance |
| `Generated/*.ir.json` | Canonical typed control-flow IR and transitive call graph |
| `Generated/*.lean` | Executable Lean transcription of the IR |
| `Generated/source-manifest.json` | Source/reference hashes, compiler and extractor versions, build properties, symbols, signatures, and IR hashes |
| `Refinement/*.lean` | Handwritten representation invariants and refinement/composition theorems |
| `Vectors/*.ndjson` | Decimal-string differential inputs and observations with reproducible seeds |
| `Generated/coverage.json` | Selected roots, reached dependencies, rejected/bypassed paths, and discharged theorem names |

Generated files are checked in for review. CI regenerates to a temporary directory and byte-compares them; normal builds do not rewrite the working tree. Generated Lean may define programs, but may not generate proofs or introduce axioms.

The exact `StateGasChargeKernel` slice validates one standalone source file and emits both its audit IR and Lean definition from the same restricted Roslyn semantic graph. The `StateGasTransitionKernel`, `TransactionGasInitializationKernel`, and `Eip8037BlockGasInclusionCheck` profiles normalize their closed graphs to canonical in-memory expression IR which drives both JSON and Lean emission. These profiles do not yet deserialize the JSON IR as the sole code-generation input or load the complete production MSBuild compilation. That is acceptable for their narrow source-to-Lean obligations because the sources, compiler mode, bound signatures, semantic outputs, and generated Lean are all hashed and regenerated together, while `Evm.slnx` independently compiles the same files in production. The general multi-kernel extractor must close both gaps before it is used for the complete-EVM claim.

Because `Nethermind.Evm` is post-processed by InlineIL/Fody, selected source roots also require a post-build IL shape/hash check or isolation from rewriting. This does not extend the theorem to CLR/JIT correctness; it prevents an unnoticed source/binary mismatch at the selected boundary.

## Compositional proof layers

Each layer has three obligations:

1. the generated C# kernel refines the handwritten Lean operation;
2. its production adapter represents inputs and applies returned effects correctly;
3. the composed production path reaches that kernel for every pinned target case.

The whole proof composes these relations:

```text
kernel step -> opcode step -> frame execution -> transaction transition -> block fold
```

The VM theorem uses a small-step semantics and an explicit step bound. Termination cannot be justified from decreasing gas alone because some control-flow steps are zero-cost. Precompiles and host primitives are named functions with proved implementations or explicit assumptions.

The transaction theorem consumes a VM observation and covers validation, intrinsic/pre-execution charging, EIP-7702 authorization effects, nonce/value/fee changes, rollback, refund cap and calldata floor, `GasConsumed`, and receipt construction. A paired theorem covers the routed standard `SystemTransactionProcessor` overrides and proves which block-internal calls are excluded from normal receipt and block-gas counters.

The block theorem folds transaction results and system operations in production order. It keeps cumulative paid receipt gas separate from cumulative execution/state block gas, proves `header.gas_used = max(block_execution_gas, block_state_gas)` where active, and covers bloom, receipt root, state root, withdrawals, rewards, and execution requests. The registered parallel decorator needs an equivalence theorem against this sequential fold before it is part of the complete claim.

## Staged coverage

| Stage | Lean specification | Production connection | Completion evidence |
| --- | --- | --- | --- |
| 0. Identity | Pinned schedule and semantic types | Pinned source/build/chain manifest | Reproducible target; exact mainnet activation recorded |
| 1. Gas and SSTORE | Reservoir, frames, complete SSTORE table | None yet | Current proofs and 22 normative vectors |
| 2. First extraction | State-gas charge result and representation invariant | Pure kernel used by `TryConsumeStateGas` | Generated definition plus success/OOG refinement theorem |
| 3. Gas kernel | Refill, merge, refund, intrinsic and block formulas | All relevant `EthereumGasPolicy` decisions | Boundary/property/mutation parity; no alternate path |
| 4. Opcode effects | Pricing and state effects for every mainnet opcode | Opcode microkernels and stack/memory/world adapters | Per-op refinement, dispatch coverage, generated differential corpus |
| 5. Frames and VM | Calls, creates, reverts, halts, returndata and access state | Frame loop and opcode-table composition | Bounded whole-EVM simulation and nested-frame corpus |
| 6. Transactions | Validation, preparation, execution and settlement | Standard `EthereumTransactionProcessor` | Receipt/state/gas equality over valid and rejected transactions |
| 7. Sequential blocks | Ordered tx fold and protocol system operations | `BlockProcessor` sequential validation path | Multi-transaction block, roots, counters, withdrawals and requests |
| 8. Production graph | Same observable semantics | Production DI graph, traced modes, parallel decorator | Reachability and observational-equivalence theorems/tests |
| 9. Complete target | Composed pinned-mainnet theorem | Standard main-processing entry point | Every claim gate in `VERIFICATION_SCOPE.md` green |

Stages are monotonic: later coverage may not replace an earlier proof with testing or hide a new assumption.

## First executable extraction slice

The first vertical slice should isolate the logic currently reached through `EthereumGasPolicy.TryConsumeStateGas` into a pure value kernel used by production. Its result preserves success/OOG and all five production gas fields. The Lean relation maps execution gas directly and maps unrefunded production spill to `stateFromGasLeft`, under explicit nonnegative and no-overflow invariants.

This slice is large enough to exercise signed/unsigned conversion, reservoir-first charging, partial spill, early failure, state-used updates, source reachability, generation, and refinement, while avoiding SSTORE's intertwined access, storage, and refund adapters. Exact-reservoir, partial-spill, exact-gas, one-short, zero, and machine-boundary cases must cross the C#, IR, generated Lean, and handwritten Lean implementations. Mutating affordability, charge order, spill, or state-used updates must break a proof or differential test.

## CI enforcement

Fast pull-request checks must:

- build the extractor, EVM, tests, and affected solutions with warnings as errors;
- regenerate and byte-compare all extraction artifacts;
- reject missing roots, call-graph drift, unsupported operations, compiler diagnostics, `sorry`, `admit`, and undeclared axioms;
- build Lean and require every manifest theorem and production-reachability entry;
- run extractor semantic/rejection tests, fixed vectors, property tests, C#/Lean parity, and mutation checks;
- assert that targeted Nethermind tests were discovered and executed;
- leave the tracked tree unchanged and enforce the hot-path performance budget.

Scheduled checks add persisted randomized seeds, EEST/Hive transaction and block replay, cross-platform generator determinism, mainnet DI graph validation, sequential/parallel equivalence, and the full mutation matrix. A green differential run cannot waive a failed extraction, theorem, reachability, adapter, or assumption gate.
