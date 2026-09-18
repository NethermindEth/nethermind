# Scope

## Included

- `TransactionProcessorBase<TGasPolicy>.ExecuteEvmTransaction` after the
  successful post-nonce dispatch handoff and before the VM call.
- `ProcessDelegations` and `IsValidForExecution`, including validation order,
  warming, account reads/writes, nonce/code/delegation effects, five gas
  fields, EIP-8037 state charges, fold, refund counter, and partial failure.
- `BuildExecutionEnvironment`, including CREATE recipient derivation, access
  warming, creation/recipient/delegated code resolution, delegated-target
  charge/read and OOG, and the typed environment rent.
- Post-intrinsic reservoir capture, dead-recipient charge, preparation snapshot
  and gas restoration, top-frame OOG handoff, top-level CREATE logical
  existence/collision/state-charge ordering, `PayValue`, null-code return, and
  the exact `VmState.RentTopLevel` input tuple.
- Standard-mainnet DI registration identity and source/compiler/reference
  closure identity.
- Successful `ValidateStatic` SetCode-domain facts: exact
  `Transaction.HasAuthorizationList` (`Type == TxType.SetCode` plus a
  nonempty list), no SetCode CREATE, and the separately bound
  `SetCodeTxValidation.ValidateAuthorizationList` acceptance path.
- The CREATE recipient projection is source-bound to the exact
  `TransactionExtensions.GetRecipient` implementation and its resolved helper
  symbols.
- Source-entry adapter facts: a fresh `StackAccessTracker` carrying only the
  production tracing flag, zero delegation refunds, entry-handoff access
  equality, and exact same-tracker flow through both outer `ExecuteEvmCall`
  calls to `VmState.RentTopLevel`,
  entry world/snapshot/gas/code identities, the source-bound
  `ExecutionEnvironment.Rent` projection, and the Ethereum gas-policy route.
- Mainnet `AmsterdamBlockTimestamp` to `Amsterdam.Instance` schedule identity,
  with `Amsterdam : NamedReleaseSpec<Amsterdam>(BPO2.Instance)` and its
  EIP-8037/EIP-8038 activation.

## Excluded

- Production state-gas arithmetic agreement for negative reservoirs or signed
  counter overflow. `RepresentationObligation` checks input representation
  ranges but does not establish reservoir nonnegativity or no-wrap reachability.
  Such states remain in the model theorem's domain and can differ from
  `StateGasChargeKernel`; production agreement on them is not claimed.
- The body of `VirtualMachine.ExecuteTransaction`, opcode execution, child
  frames, frame/tail settlement, fees, receipts, destroy-list finalization,
  block gas accounting after the boundary, and external world/code/gas
  implementations as semantic definitions.
- Alternate forks, system transactions, simple-transfer fast paths, parallel
  transaction routes, unchecked source, and arbitrary caller-chosen semantic
  selectors.

The package makes no claim that a source hash, metadata hash, or compiler
identity alone proves protocol behavior. Such values are closure obligations;
the source-bound IR, independent reference, and explicit representation/adaptor
obligations are separate evidence. Imported ordinary-dispatch and authorization-
fold theorems are currently checked only at concrete projected inputs for
identity; no cross-package semantic result equality is claimed.
