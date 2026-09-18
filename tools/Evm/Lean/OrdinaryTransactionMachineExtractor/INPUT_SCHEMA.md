# Input and adapter schema

The generated kernel receives a narrow model input, not a C# object or executable callback.
Its `MachineInput` contains a block header projection, raw option bits, ordinary/system and
BAL/parallel route bits, the exact simple-transfer eligibility observations
`isCodeOverridable = false`, `hasAuthorizationList = false`, and
`forceSimpleTransferDisabled = false`, recipient/code/delegation predicates, and an opaque
`TracerSeed`.
`startNewBlockTrace` deliberately resets that seed to a sequential base tracer with index `0`,
empty receipt and gas histories, zero cumulative receipt gas, and the supplied header-gas value.
The refinement `FreshSequentialTracer` adapter records this reset as an explicit premise, including
the header-gas relation, and therefore also covers empty input without inheriting caller state.

Each `SettledEntry` is an already-settled terminal observation: terminal transaction projection,
receipt fields, gas fields, cumulative gas totals, recipient, output/log/state-root oracles,
the typed nested-tracer and current-receipt-tracer forwarding flags, status, result constructor,
and an explicit well-formed bit. A success entry is admitted only when
it is well formed, has success status and `.ok` result, and its receipt/gas observations match the
ReceiptTerminal kernel output fieldwise. A malformed `.ok` entry rejects before changing the
current receipt/gas prefix. A truthy `EvmException` or invalid result is a named excluded route;
its production failure poststate is not modeled.

`NormalReturnPremises` is an opaque per-run adapter record. Its global fields cover context
installation, `Process`/`ExecuteCore`/ordinary execute, static/stateful admission, nonce and
preparation, pre-execution commit, available gas, simple transfer, settlement, transaction
commit/finalization, direct executor return, the `TransactionsExecuted` subscriber, post-fold
`CommitState`, and receipt terminal callbacks. Its per-entry list records `StartNewTxTrace`,
`Execute`, and `EndTxTrace` normality for every settled entry. The refinement layer additionally
requires proof-carrying `SourceAdapterPremises`: each field proves the corresponding Boolean
observation is true, and `EntrySourceNormalityChain` relates the per-entry proof to the same
position in the settled list while carrying that entry's settled-terminal invariant. The machine
returns rejection unless all required fields and list
lengths are true; no field of type bare `Prop` can be inhabited independently of its observation.

The independent specification uses its own receipt, gas, state, event and outcome structures.
The refinement maps terminal oracle identifiers and all receipt/gas dimensions fieldwise. It does
not import the generated model into the specification and does not use `TransactionResult.Equals`
or any whole-result equality. Source, normal-return, and observation correspondence remain named
obligations rather than hidden axioms. The primary success theorem proves the multi-entry fold by
induction from a terminal observation chain and local fieldwise bridges; it does not take a
whole-run relation as a premise. Its theorem domain is explicitly an adapter-supplied settled
list fold; the package does not relate that list to `Block.Transactions` or prove that production
transaction execution constructed it.
