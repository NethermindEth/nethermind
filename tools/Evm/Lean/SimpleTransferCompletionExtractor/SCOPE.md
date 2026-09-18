# Scope and boundary

The admitted source entry point is the private ordinary-mainnet
`TransactionProcessorBase<TGasPolicy>.ExecuteSimpleTransfer` method in
`src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs`. The handoff is
the exact byte-pinned identity and field shape of `OrdinaryPostNonceDispatchExtractor`'s
`SimpleHandoff`; that extractor remains under separate review, and no semantic acceptance or
composition theorem is inherited from its artifacts. No pre-handoff behavior is re-proved here.
The three dependency bytes are pinned to IR `850ca2852786e8978f046d7b7b3999d8624108cf3e8aee04cab7baaee274432d`,
manifest `df941ccbc98cbf479f1b8accfff2f2ad67fd0076f98975e0042e4bb8e1f505f3`, and generated Lean
`645ee68e4edf2e4d81d3e27da202a588d57b9f8ffdc883f035e3c55a0c1f475c`; these hashes identify the
input shape only and import no semantic acceptance or composition theorem.

This package proves a generated-model versus independent-reference-model result relation: scalar
fields, receipt payloads, and event order are exact, while ReportAccess address/storage carriers
are compared extensionally. It is not a production semantic-equivalence theorem. Roslyn admission covers source identity, exact typed
signatures and argument order, source order (including `PayValue` before the recipient write),
finite guards, and no-frame CFG reachability. The closed operation terms for settlement, fees,
finalization, world state, and callbacks are handwritten model terms selected by that admission.

The model result ends with a `ReceiptContinuationInput` containing the callback payload, including
the actual `tracer.IsTracingReceipt` value. The boundary is normal-return request issuance: callback
exceptions and partial callback prefixes are excluded. Receipt folding and receipt-root
calculation are excluded; a `false` tracing flag is modeled as absence of the request, not as receipt
folding.

World reads/writes and state-root before/after values are opaque request-boundary inputs. The
settlement and state-charge kernel artifacts are semantic model imports consumed through typed
kernel responses under explicit no-wrap premises; the OrdinaryPost artifacts above are identity-only
and their semantic composition is not re-proved here. `ReportAccess` is set-valued by address
and storage-cell membership, so live `HashSet` enumeration order is outside the boundary. Transfer
topics are derived by the generated boundary's bounded `addressHashProjection` from the
source-bound sender and recipient. This abstracts `Address.ToHash().ToHash256()` without proving
Keccak correctness; the transfer-log request derives its data and fixed fields from the source-bound
value/log constructor. The result view is a
status/receipt projection and does not claim every CLR exception or description field.

The universal refinement theorem additionally requires the explicit `normalReturnDomain` premise
(`TraceAdapter.normalReturn = true`), which excludes callback-exception prefixes from the claimed
request-issuance trace.

VM, cryptography, CLR integer implementation, database/trie, system, parallel, XDC, Taiko, and BAL
behavior remain outside the package and require separate adapter/lowering claims.
