# Authorization state-gas fold extraction

This package is a fail-closed, source-derived composition slice for the
current EIP-7702/EIP-8037 authorization state-gas admission path.  It does not
change production behavior.  Roslyn extracts typed semantic IR from the exact
current sources, emits theorem-free Lean definitions, and pins every accepted
leaf dependency by source-manifest hash and theorem signature.

## Exact claim

The admitted domain is the standard `EthereumGasPolicy` transaction path with
these premises:

1. Transaction admission starts with zero intrinsic authorization state gas:
   `IntrinsicGasCalculator.AuthorizationListCost` returns state cost zero and
   `EthereumGasPolicy.Calculate` projects that zero into the intrinsic policy.
2. `TransactionProcessorBase<TGasPolicy>.Execute` calls its virtual
   `CalculateAvailableGas` gate before either transaction-execution route; the
   admitted base implementation forwards directly to
   `TryCreateAvailableFromIntrinsic`, which invokes the exact generalized
   `TransactionGasInitializationKernel.TryCreate` leaf and projects all five
   gas-policy fields.
3. For each normalized *valid* authorization effect, `ProcessDelegations`
   derives EIP-161 logical existence with `IsDeadAccount`, preserves physical
   leaf existence separately for `CreateAccount` versus `IncrementNonce`, and
   charges new-account state gas only for logical nonexistence. It then charges
   first-account-write execution gas and the first non-clear delegation's
   per-authorization state gas in that order.
   The account-write step is the extracted `UpdateGas` guard
   `GetRemainingGas(in gas) < gasCost`, which sets `gas.Value = 0` on failure
   and invokes `ConsumeRaw(ref gas, gasCost)` on success.
   `TryConsumeStateGas` is separately admitted through its exact
   `StateGasChargeKernel.TryCharge` call, out-of-gas-before-mutation branch,
   and ordered projection of all five result fields.
4. `authorizationStateGasUsed` is the checked post-minus-pre
   `StateGasUsed` delta, and `FoldTopFrameStateGas` runs only after the whole
   authorization loop succeeds, with `gasAvailable` as the gas argument and
   `executionIntrinsicGasStandard` as the baseline argument.
5. The five-field fold writes exactly these four production fields, in source order:
   `baseline.StateReservoir += stateGasUsed`,
   `baseline.StateGasUsed += stateGasUsed`,
   `gas.StateGasSpill = 0`, and
   `gas.StateGasSpillRefunded = 0`.  `Value` is intentionally untouched.

The handwritten refinement proves its state-charge function equal to the
common `ProductionGas.tryConsumeStateGas` semantics and interprets an
authorization as a list of primitive state and execution charges; it is not a
copy of the generated nested control flow. It
uses unbounded `Nat`/`Int` and names
`FitsUInt64`, `FitsInt64`, `ChargeNoWrap`, `DeltaNoOverflow`, `FoldNoOverflow`, and
`ProcessNoWrap` as explicit width, nonnegative-cost, and no-wrap obligations,
but does not prove that every fixed-width C# intermediate satisfies them or
that fixed-width execution is equivalent to these unbounded definitions; that
equivalence remains outside this package's claim.  `ProcessNoWrap` records
entry-state and cost ranges; per-transition charge, delta, and fold conditions remain
obligations for any fixed-width correspondence. A failed charge is modeled
as a partial `ProcessDelegations` result retaining every prior successful
charge. The extractor checks that the snapshot is captured inside the
EIP-7702/EIP-8037 preparation guard and that restore plus both gas resets stay
inside the later top-frame EIP-8037 out-of-gas branch. The optional restore is
the first direct statement of that branch and both resets are direct sibling
statements after it, so they do not become conditional on snapshot presence.
The caller-visible
projection is modeled by `callerVisible`; this does not claim that
`ProcessDelegations` itself is atomic.

The refinement includes exact failure vectors for both within-authorization
failure points: an account-write execution OOG retains the successful
new-account state charge and clears `Value`, while a later per-authorization
state OOG retains the successful new-account state and account-write execution
charges and their resulting execution fields.

The normalized authorization-effect list is an assumed adapter boundary; the
unused raw `eip8038` flag is deliberately absent because its only modeled
consequence is the already-normalized `accountWriteExecutionCost`. This
package does not prove authorization validation, signature recovery, nonce,
chain-id or code checks, the correctness of world-state existence reads,
journal or snapshot correctness, authority mutation, EIP-8038 pricing, the whole
authorization loop, gas settlement, receipts, or dependency injection.

## Source and dependency boundary

The extractor reads these complete source files:

- `src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs`
- `src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs`
- `src/Nethermind/Nethermind.Evm/IntrinsicGasCalculator.cs`
- `src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs`

The checked dependency identities are the current generalized
`TransactionGasInitialization`, `StateGasCharge`, and `StateGasTransition`
manifests, their generated Lean artifacts, and the accepted theorem
signatures. A changed admitted source shape, call order, argument list,
polarity, field projection, manifest, theorem signature, or artifact hash
fails extraction. Complete source hashes are recorded for review and artifact
identity; unadmitted source edits are not claimed to be rejected wholesale.

## Artifacts and verification

- `Generated/AuthorizationStateGasFold.ir.json` — typed semantic IR.
- `Generated/AuthorizationStateGasFold.source-manifest.json` — exact source,
  dependency, theorem-signature, and artifact identities.
- `Generated/AuthorizationStateGasFold.lean` — generated definitions only.
- `Refinement/AuthorizationStateGasFold.lean` — independent specification,
  machine/no-wrap premises, partial-failure model, and checked refinement.

From this directory, after the exclusive verification window is released:

```powershell
dotnet build .\AuthorizationStateGasFoldExtractor.csproj -c Release -warnaserror -p:SaveDiskSpace=true
dotnet build .\Test\AuthorizationStateGasFoldExtractor.Test.csproj -c Release -warnaserror -p:SaveDiskSpace=true
dotnet run --project .\Test\AuthorizationStateGasFoldExtractor.Test.csproj -c Release --no-build -- --minimum-expected-tests 77 --no-ansi --progress off
lake build AuthorizationStateGasFoldExtractor.Refinement.AuthorizationStateGasFold
lake env lean -DwarningAsError=true .\Generated\AuthorizationStateGasFold.lean
lake env lean -DwarningAsError=true .\Refinement\AuthorizationStateGasFold.lean
```
