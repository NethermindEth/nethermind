# Source audit

`Admission/ProductionClosure.txt` is the complete reviewed source and artifact
dependency list; its own SHA-256 identity is pinned in the extractor. The
extractor requires exact SHA-256 equality before it builds any artifact. The compiler inventory is itself a pinned dependency and is
rechecked for file set, SHA-256, MVID, assembly name, metadata references, and
selected assembly uniqueness.

The compiler artifacts are built with `SOURCE_DATE_EPOCH=1789035784`, the
pinned source commit's epoch. This fixes the assembly metadata timestamp so
clean rebuilds can reproduce the reviewed byte and MVID identities.

The semantic units compile the admitted C# sources against the deterministic
compiler/reference closure. The processor, frame-state, world-state, gas,
code, core, fork, and DI units retain their real source files. Each admitted node is
bound through a Roslyn semantic model and `IOperation`; every admitted processor
member (including `ValidateStatic` and `PrepareDeployment`) has complete reachable
CFG exit/back-edge evidence. Error, candidate, ambiguous, missing, or
unreachable bindings fail closed. Branch counts, overload counts, operation
identities, route registrations, and the VM handoff overload pair are fixed.

The closure also admits the real `MainnetSpecProvider` and `Amsterdam` fork
sources. Their semantic checks require the mainnet Amsterdam timestamp and
`Amsterdam.Instance` schedule edge, `NamedReleaseSpec<Amsterdam>` over
`BPO2.Instance`, and the EIP-8037/EIP-8038 activations. The fork sources are
route identities, not a replacement for the independent reference.

`VirtualMachine.ExecuteTransaction` is a boundary identity. The method body is
not lowered, and no empty implementation is used as an execution oracle.

The ordinary post-nonce and authorization-fold dependencies are checked by a
separate identity theorem at concrete projected sibling inputs. This package
does not claim that either sibling result is equal to the preparation
transition; a cross-package semantic adapter is intentionally outside the
current proof claim.

The imported ordinary-dispatch generated and reference Lean files are directly
byte-pinned alongside its IR, manifest, and refinement. The sibling manifest's
Lean path and hash must match the actual imported generated file.

The current representation obligation admits negative reservoirs and does not
prove that signed state-gas counters cannot wrap during production execution.
The model-to-model equality does not establish agreement with production
`StateGasChargeKernel` for those states. Nonnegativity and no-wrap reachability
remain an explicit residual production obligation.

The production-entry adapter is explicit in the normalized plan and remains a
conditional refinement premise: the extractor admits the exact C# bindings,
but it cannot manufacture a Lean representation of a live `WorldState`,
`StackAccessTracker`, or `Snapshot` journal. It therefore requires the freshly
allocated tracker to have no pre-warmed accounts, storage, or reads while
preserving `tracer.IsTracingAccess`, requires zero initial delegation refunds,
and ties the fresh entry-tracker projection (the enriched package handoff
access) to entry/pre-execution world facts, preparation gas,
intrinsic-gas standard, the source-bound `ExecutionEnvironment.Rent` code
projection, context repository, gas policy, and the exact VM boundary to the
handoff. The final tracker observation is carried by the generated result and
VM-input access fields after preparation; it is not constrained to be fresh.
When authorization is enabled, the
snapshot token is only the opaque result of the admitted `TakeSnapshot` call
(or the source-defined `Snapshot.Empty`/`EmptyPosition == -1` sentinel); the
adapter does not assert journal internals or restore correctness. The exact
source guard `spec.IsEip7702Enabled && tx.HasAuthorizationList` is separately
bound, with `Transaction.HasAuthorizationList` itself required to retain the
exact `Type == TxType.SetCode` guard. The nested `spec.IsEip8037Enabled` guard
is also separately bound, and the typed adapter preserves the corresponding
`hasPreExecutionSnapshot == (eip7702 && hasAuthorization && eip8037)` coherence
condition. Successful static admission additionally excludes every SetCode
CREATE transaction, including one with an empty modeled list, and rejects an
empty authorization list through the separately admitted
`SetCodeTxValidation.ValidateAuthorizationList` call. The two outer
`ExecuteEvmCall` calls are semantically required to pass the fresh local
tracker as `in accessedItems`, which is then required to flow to
`VmState.RentTopLevel`. CREATE recipient derivation is separately bound to the
exact `TransactionExtensions.GetRecipient` implementation and its semantic
`ContractAddress.From`/`IsSystem` targets.
The complete `inputFactsCoherent` predicate is a separate refinement premise,
covering account, authorization, recipient, code, and value-transfer facts in
addition to the source-entry adapter. Invalid projected facts are outside the
universal transition equality; generated and reference diagnostic strings for
those invalid inputs are not required to agree.
General internal model lemmas may still exercise
warm/refund states, but those states are outside the production-entry theorem
domain.
