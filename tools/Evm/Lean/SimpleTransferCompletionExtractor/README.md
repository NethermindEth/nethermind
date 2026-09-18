# Simple-transfer completion extractor

This package is a hash-pinned, audited model-to-model artifact for the ordinary standard-mainnet
`TransactionProcessorBase<TGasPolicy>.ExecuteSimpleTransfer` request boundary. Its input is the
`SimpleHandoff` shape identified by the exact byte-pinned
`OrdinaryPostNonceDispatchExtractor` artifacts; that dependency is still under separate review,
and no semantic acceptance or composition result is inherited from those identity bytes. The
settlement/state-charge artifacts are separate semantic kernel imports used as typed model
responses; they do not make the OrdinaryPost identity bytes semantic proof inputs. The output is the
generated `SimpleComplete` model and a concrete `ReceiptContinuationInput` model. `Reference/` is
an independently structured handwritten model and `Refinement/` proves that the two model results
agree under the package's explicit numeric, kernel, access-set, and normal-return premises; transfer
topics use the generated bounded address projection, and only access-carrier enumeration is
quotiented.

The pinned handoff identities are the IR SHA-256
`850ca2852786e8978f046d7b7b3999d8624108cf3e8aee04cab7baaee274432d`, manifest SHA-256
`df941ccbc98cbf479f1b8accfff2f2ad67fd0076f98975e0042e4bb8e1f505f3`, and generated-Lean
SHA-256 `645ee68e4edf2e4d81d3e27da202a588d57b9f8ffdc883f035e3c55a0c1f475c`. They identify the
input shape only; no semantic acceptance or composition theorem is imported.

The claim is deliberately narrower than production equivalence. Roslyn admits exact source
identities, resolved operation signatures, source order, CFG reachability, and the finite guard
surface. The emitted operation terms are closed formulas for the two models; they are not a
compiler for the bodies of `PayValue`, `Refund`/`PayRefund`, `ShouldRefundGas`, `PayFees`,
`FinalizeTransaction`, world-state forwarding, tracer dispatch, or callback exception behavior.
The source and dependency hashes therefore audit the boundary and its admission, not composition
of production implementations.

The model records typed world/tracer/receipt request payloads, but assumes normal-return request
issuance at those boundaries. This is an explicit input domain: `TraceAdapter.normalReturn = true`
is required by `universal_refinement`; callback-exception prefixes are therefore not represented.
World reads/writes and before/after state-root values are opaque
adapter inputs. `ReportAccess` is interpreted extensionally as sets of addresses and storage
cells; live `HashSet` enumeration order is not part of the claim. Transfer topics are derived by
the generated boundary's bounded `addressHashProjection` from the source-bound sender and
recipient. This is an abstraction of `Address.ToHash().ToHash256()` and does not prove Keccak
correctness; the generated transfer-log request derives its signature/data/address fields from the
source-bound transaction/log constructor. The
`TransactionResult` view is the model's status/receipt projection, not the complete CLR result
including every exception or description field. Receipt folding and receipt-root calculation are
outside the boundary.

The source admission includes `PayValue` in its exact source-order sequence before the recipient
balance request, the EIP-8037 state-charge branch, the four no-frame `CompleteWithoutFrame` edges,
and the `FailContractCreate -> Complete` bypass. A source mutation that changes an admitted guard,
cost, overload, receiver, argument, order, CFG edge, or transfer-log payload is rejected or
changes the rebound model artifact.

The package pins the live transaction-processor source and all selected closure identities. Its
current audit records one confirmed production defect, fixed in the live source: the EIP-8037
state-charge OOG no-frame completion path omitted `ReportAccess`. No additional production defect
was confirmed by this bounded audit.

## Verification

Run `Verify.ps1` from the serialized build lane. It builds the extractor and tests with warnings as
errors, regenerates into a temporary directory, checks deterministic checked-in artifacts, runs
the complete discovered test count, and typechecks the four Lean targets under the pinned
`leanprover/lean4:v4.33.1` toolchain. The editing lane does not invoke these commands.
