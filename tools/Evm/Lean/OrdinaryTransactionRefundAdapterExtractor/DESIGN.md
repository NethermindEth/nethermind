# Ordinary transaction refund adapter

Status: independently accepted conditional source-audited refund-helper refinement. Independent specification/vector targets, live source admission, exact adapter refinement, the complete package gate and independent executable review pass. This package does not establish the full transaction, block, client, or Ethereum execution claim.

## Source and claim

The bounded entry is a refund-helper invocation in the standard `EthereumTransactionProcessor` lineage, with `TGasPolicy = EthereumGasPolicy`. It starts after the caller has selected a refund route, not at transaction admission or VM entry. It owns `Refund`, `CompleteEip8037Halt`, `RefundRevertedExecutionStateGas`, `RefundOnContractCollision`, `RefundOnTopLevelHalt`, `RefundFailedEip8037Gas`, `CalculateInitialStateReservoir`, `ShouldRefundGas`, and standard `PayRefund` in `TransactionProcessor.cs`, plus their exact arithmetic, policy, projection, and dispatch closure.

The theorem computes the thirteen inputs to the accepted `TransactionSettlementKernel` on the normal tail, the six returned `GasConsumed` fields on every admitted route, all five working gas-policy fields, caller-visible gas, and the optional sender-credit request. Exceptional EIP-8037 halt has its own result projection; it is not modeled by calling the normal settlement kernel with `isError = true`.

## Raw observations, not answers

Inputs are a tagged entry route, transaction gas limit and fee projections, gas price, execution-option bits, fork flags, raw substate error/revert/refund/destroy observations, the authorization code-insertion **count**, incoming five-field gas, intrinsic-standard and floor gas snapshots, the post-authorization intrinsic reservoir baseline, and the top-level CREATE-state-charge observation. The five fields are unsigned execution gas and signed state reservoir, state used, state spill, and refunded spill.

The interface does not accept `GasConsumed`, pre-refund gas, code-insertion execution refund, a halt floor, post-halt gas, settlement inputs/results, payment amount, or a completed refund result. These are derived internally. Authorization processing and VM execution remain upstream obligations: a raw snapshot is not a proof that an arbitrary caller can produce it.

| Entry | Selected helper path | Caller-visible gas |
| --- | --- | --- |
| Success / REVERT | `Refund`, with optional CREATE refill, then normal settlement | Original input; working gas is a local copy |
| VM error | `Refund`, then EIP-8037 halt when enabled | Original input; working gas is a local copy |
| Preparation OOG | Direct `CompleteEip8037Halt` after caller restoration | Updated through the `ref` argument |
| CREATE state-charge OOG | Direct `CompleteEip8037Halt` | Updated through the `ref` argument |
| CREATE collision | `RefundOnContractCollision`, then halt when enabled | Original input; helper uses a local copy |
| Failed code deposit | Direct halt under EIP-8037; legacy full-gas result otherwise | Direct-halt `ref` updates under EIP-8037 |

Failed deposit has `IsError = false` in the reconstructed substate, so error flags alone cannot select the route. The current failed-deposit site does not select `RefundOnFail`'s EIP-8037 branch. The separate no-code `ExecuteEvmCall` fast path is outside these helper entries.

## Required derived computations

1. On EIP-8037 CREATE REVERT, apply the exact accepted state-refund transition with `trackSpillRefund = false`, using intrinsic state gas as the floor. Do not merge this reversible target-creation refill with durable authorization accounting.
2. Derive code-insertion execution refund from the count and exact Ethereum policy. It returns zero without changing gas under EIP-8037; legacy arithmetic remains fixed-width. Positive code-insertion refund at an abstract halt-helper boundary is not claimed reachable from pinned EIP-8037 authorization processing.
3. Bind inherited `IGasPolicy.GetPreRefundGas`: an `Int128` difference of transaction limit, execution gas, and signed reservoir, with fallback to the full limit if outside `ulong`. Natural subtraction is not equivalent on unrestricted inputs.
4. For halt, derive the initial reservoir, refunded intrinsic state, and remaining intrinsic-state floor using the source's signed casts and subtractions. Refund reverted state, then reset reservoir/used/spill, then clear execution gas. `ResetForHalt` preserves refunded spill; `ClearExecutionGas` changes only execution gas. Terminal refunded spill may exceed the reset spill of zero.
5. For normal settlement, derive all thirteen arguments from the resulting working gas and raw observations. Project all six kernel output fields without conflating operation gas and maximum gas.
6. For halt, compute its independent `(spent, spent, blockExecution, stateUsed, spent, executionRefund)` projection.
7. Evaluate `!gasPrice.IsZero && (!SkipValidation || MaxFeePerGas != 0 || MaxPriorityFeePerGas != 0)`. Normal refund calls `PayRefund` even when its amount is zero; the halt helper additionally requires `spent < gasLimit`. Standard `PayRefund` calls the world-state hook only for a nonzero `UInt256` amount.

## Source admission and trust boundary

The admission target is the actual selected Release / non-ZK source and compiled reference closure. The source roster, compiler assembly identities/MVIDs/bytes, selected references, build selection, exact ordinary dispatch lineage, generic policy binding, helper declarations, parameter ordinals, expression operations, and implicit call paths must be checked and serialized. Compiler-support bodies are not verified merely because they compile. A reachable support callback, overload, conversion, property, initializer, or helper outside the explicitly owned closure or named external leaf is rejected.

Synchronous callable checks exclude conditional, async, iterator, extern, bodyless, hidden local-function, and unadmitted override/interface routes. Semantic trees reject directives and disabled text; compiler-support source selection must match pinned build symbols. `--check` must rebuild the source-derived admission and artifacts, not trust a repinned serialized answer.

The world-state `AddToBalance` call, transaction/spec/substate/collection observation normality and non-reentry, debug-call erasure under the exact Release symbols, and upstream entry-route/snapshot provenance remain explicit external-effect premises. The pinned `UInt256` multiplication/conversion/zero-test targets have an explicit unsigned 256-bit representation and modulo-arithmetic leaf obligation: this package does not prove that external library implementation merely by pinning its assembly. Standard-mainnet dispatch excludes system, rollup, plugin, and subclass refund overrides. No theorem here verifies arbitrary world-state implementations or effects after an exceptional hook return.

## Arithmetic and proof boundary

Generated Lean is theorem-free and reflects fixed-width operations, including signed state fields, casts, `Int128` pre-refund arithmetic, unchecked `ulong`/`long` operations, and `UInt256` payment. An independent specification may import accepted handwritten settlement/state-transition specifications, not the generated adapter. Refinement must expose exact machine semantics first; natural monetary/gas claims separately require width, nonnegative-domain, no-overflow, refund-bound, and successful-reservation premises. The existing natural `TransactionGas` bridge cannot establish preservation of the original five-field split because it deliberately collapses the reservoir into total remaining gas.

Acceptance requires strict IR/manifest schemas, deterministic atomic manifest-last publication, live-source checks, compile-first semantic mutations (including coordinated source/pin/IR changes), direct Lean targets, vectors for each route and arithmetic boundary, complete descendant export/standard-only axiom inventory including transitive upstreams, package Verify, and a serialized warning-as-error solution build followed by live post-check. No `native_decide`, export exclusions, or weakened trust boundary is permitted.

## Verification evidence

The source has been inspected and the two distinct settlement shapes, local-versus-ref ownership, inherited generic policy defaults, halt ordering, and surviving refunded-spill field are understood. No production bug was found in this audit. The exact generated refinement composes the accepted scalar settlement and state-transition semantics with the adapter's reset/clear, route and ownership semantics. The complete package gate passes 104 C# tests, 720 schema controls, deterministic re-extraction, warning-as-error Lean targets, eight finite semantic mutation controls, and the standard-only 1,005-export descendant audit. The mutation harness keeps the full universal-proof baseline and requires machine-valid concrete refinement equalities to decide false on compile-valid mutants; resource or elaboration failures are not detection evidence. Independent executable review accepted this conditional helper-entry scope. The shared Release solution build and fresh live-source checks passed; the final warning-text-only regeneration reran the full package gate. Exact accepted hashes and logs are in [README.md](README.md). Heavy builds remain serialized with other extraction milestones.
