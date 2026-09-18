# Conditional boundary

## Included conditional boundary

The standard, non-zkEVM Amsterdam `EthereumTransactionProcessor`, `EthereumGasPolicy`, exact `ExecutionOptions.Commit`, and sequential base `BlockReceiptsTracer` path are selected. The shared transaction is non-CREATE. The actual final source-attached `Preparation.run` result must have `status = vmBoundary` **and** `vmInput = some frame`; its code is present. A status tag alone is insufficient because preparation also constructs intermediate values with that tag.

The opaque VM observation is the actual normal return from executing that computed top-level frame, not a caller-provided settlement. It includes the constructor-constrained returned substate and all five post-return frame gas fields. A precompile can be behind a present code descriptor; including its typed return does not verify the precompile or VM execution.

The destroy list is empty. World-state, code, price, logging/tracing, nested receipt callbacks, commit and disposal hooks return normally, obey their stated identity/frame conditions and do not re-enter or mutate unrelated live inputs. Every write is modeled separately; the contract does not assume the final gas counters, receipt, status or transaction result.

## Identities and external obligations

The preparation, VM call, refund, fee and receipt boundaries refer to the same transaction, release spec, header, sender, executing account, gas price, access tracker and top-level snapshot. The receipt tracer has the same current transaction/header and coherent current index/history. Address, bytes, log, hash, error and price projections are explicit abstract representations, not cryptographic or serialization proofs.

The VM-return contract retains the exact entry gas, environment, access tracker and snapshot. `InitialStateGasUsed` is the frame-entry state usage, including any pre-frame dead-recipient account charge. Top-level non-CREATE sets `NewAccountCharged = false` and `IsCreateStateGasCharged = false`; neither assertion implies zero state gas.

REVERT gas at this boundary has already passed `RefundRevertedTopLevelStateGas` using that entry baseline. The composition must not refund it a second time or substitute the intrinsic reservoir/halt floor. Exceptional VM paths may already restore the top-level snapshot; the transaction processor's subsequent restore is still a distinct event.

Machine-width, signed-gas nonnegativity, conversion bounds and no-wrap obligations must be stated for each arithmetic bridge. Mathematical `Nat` truncation is not evidence that a negative production `long` is impossible. UInt256 fee/payment arithmetic is modular unless an explicit no-wrap theorem applies.

The raw opcode metric is signed `Int32`; the reference stores an `Int` and requires `0 ≤ count ≤ 2147483647` before its event projection to `Nat`. The private `Execute` caller derives `restore` and `commit`; the callee's options value alone does not establish those independent Boolean parameters. The external entry relation explicitly binds `restore = false` and `commit = true` to that caller.

The accepted preparation theorem is model-to-model under its fixed-width, source-entry and coherent-input obligations. It does not establish production state-gas kernel agreement or absence of signed wrap during preparation. Completion must retain that precise upstream gap, not assume equality of an entire completed preparation result or describe the composition as end-to-end production preparation correctness.

## Excluded

CREATE, code-null/simple-transfer, top-frame preparation OOG, warmup, restore, buildup, skip-validation, parallel processing, system transactions, alternative processor/gas-policy/tracer overrides, nonempty destroy lists and abnormal or re-entrant hooks are outside this first slice. Transaction admission and complete VM execution remain upstream/external obligations, not conclusions of this composition.

Database durability, state/receipt root algorithms, cryptographic precompiles, CLR/JIT/AOT, allocation failure, asynchronous scheduling, networking and RPC are unproved. External commit invocation/order is not database correctness.

## Claim discipline

The independently accepted bounded claim is conditional source-audited refinement of the generated continuation, with preparation remaining a model stage. The appropriate claim names this bounded continuation, the trusted restricted extractor and the published obligations, never “Nethermind is formally verified.” The bridge computes and connects the preparation/refund/receipt stage inputs; it does not discharge their external production provenance, the preparation correspondence gap, or complete VM execution.
