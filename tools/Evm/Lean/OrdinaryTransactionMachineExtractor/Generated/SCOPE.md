# Generated artifact status

`OrdinaryTransactionMachine.lean`, its IR, and its source manifest are static drafts only.
They intentionally carry `static-draft`/`re-emit` markers and are rejected by `--check` until
the serialized lane runs the Roslyn extractor. The extractor then emits these files from the
compiler-closed semantic IR and replaces them atomically.

The generated kernel is theorem-free and models only `OnlyOkTerminal`: exact Commit, ordinary
standard-mainnet, sequential BAL-disabled direct-inner dispatch, live-recipient simple transfer,
the three exact fast-path eligibility guards, and ordered receipt/gas accumulation plus
conditional nested/current tracer forwarding through the pinned ReceiptTerminal kernel.
The source admission also pins the standard validation-module registration, decorated executor
constructor/capture chain, execute-adapter factory, and exact block-context/Commit forwarding.
The refinement's proof-carrying normal-return premises, adapter-supplied settled receipt observations,
malformed-entry rejection, fresh tracer reset, and empty input are explicit model
inputs/relations. The model does not connect settled entries to source block transactions. It is
not a C# interpreter or a production-equivalence certificate.
