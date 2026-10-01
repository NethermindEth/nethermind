# Scope and boundary

The admitted source entry point is the private ordinary-mainnet
`TransactionProcessorBase<TGasPolicy>.ExecuteSimpleTransfer` method in
`src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs`. The local
handoff bundle has direct source provenance through the bound `Execute/6`, candidate preparation,
code lookup, available-gas result, and typed `ExecuteSimpleTransfer/15` dispatch. Earlier
validation, gas purchase, and nonce update are external prefix premises; no prior post-nonce
proof is imported.

The checked-in schema-1 generated artifacts are stale and unaccepted after the caller-admission
edits. The following model theorem is not yet a source-attached production theorem.

This package proves a generated-model versus independent-reference-model result relation: scalar
fields, receipt payloads, and event order are exact, while ReportAccess address/storage carriers
are compared extensionally. It is not a production semantic-equivalence theorem. Roslyn admission covers source identity, exact typed
signatures and argument order, source order (including `PayValue` before the recipient write),
finite guards, and the source-bound no-frame branch sites without claiming their CFG edges. The closed operation terms for settlement, fees,
finalization, world state, and callbacks are handwritten model terms selected by that admission.

The model result ends with a `ReceiptContinuationInput` containing the callback payload, including
the actual `tracer.IsTracingReceipt` value. The boundary is normal-return request issuance: callback
exceptions and partial callback prefixes are excluded. Receipt folding and receipt-root
calculation are excluded; a `false` tracing flag is modeled as absence of the request, not as receipt
folding.

World reads/writes and state-root before/after values are opaque request-boundary inputs. The
settlement and state-charge kernel artifacts are semantic model imports consumed through typed
kernel responses under explicit no-wrap premises. `ReportAccess` is set-valued by address
and storage-cell membership, so live `HashSet` enumeration order is outside the boundary. Transfer
topics are derived by the generated boundary's scalar `addressHashProjection` from the
source-bound sender and recipient. Production `Address.ToHash().ToHash256()` zero-left-pads the
20-byte address to 32 bytes; the current model does not yet prove the required `UInt160` bound or
byte-encoding bridge. The transfer-log request derives its data and fixed fields from the
source-bound value/log constructor. The result view is a
status/receipt projection and does not claim every CLR exception or description field.

The universal refinement theorem additionally requires the explicit `normalReturnDomain` premise
(`TraceAdapter.normalReturn = true`), which excludes callback-exception prefixes from the claimed
request-issuance trace.

VM, cryptography, CLR integer implementation, database/trie, system, parallel, XDC, Taiko, and BAL
behavior remain outside the package and require separate adapter/lowering claims.
