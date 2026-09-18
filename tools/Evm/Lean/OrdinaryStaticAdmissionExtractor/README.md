# OrdinaryStaticAdmissionExtractor

This package admits two return-value helper projections from the standard-mainnet Ethereum processor:

1. `TransactionProcessorBase<EthereumGasPolicy>.ValidateStatic`, including its virtual `ValidateGas` call.
2. `EthereumGasPolicy.TryCreateAvailableFromIntrinsic`, closed through
   `TransactionGasInitializationKernel.TryCreate`.

It is deliberately not a transaction transition, a VM model, or a proof of complete transaction admission.
`ValidateStatic` rejection logging is outside the projection; the production method itself is therefore not claimed
to be side-effect-free.
The generated Lean is theorem-free and imports the pinned canonical EIP-8037 initializer kernel. The handwritten
reference is independent of the generated admission predicates; `Refinement/OrdinaryStaticAdmission.lean` proves
the generated static result fieldwise and proves the wrapper/initializer result fieldwise.

Extraction is exact, fail-closed Roslyn syntax admission rather than a complete project `SemanticModel` or proof of
C# operational semantics. The package validates the admitted receiver/argument/local/result shapes and rejects
known instance-shadow and override cases in the pinned sources; compiler symbol resolution beyond that closure is
an explicit trust boundary.

The standard synchronous route is pinned as `BlockProcessingModule -> EthereumTransactionProcessor ->
EthereumTransactionProcessorBase -> TransactionProcessorBase<EthereumGasPolicy>`. The standard BAL worker route
is pinned separately through `TransactionProcessorFactory<EthereumGasPolicy> ->
TransactionProcessor<EthereumGasPolicy>`. The source is virtual, so `SystemTransactionProcessor`, XDC, Taiko,
and plugin overrides are outside the claim. The helper input retains only the exact
`opts.HasFlag(ExecutionOptions.SkipValidation)` Boolean projection; raw/composite option routing is not modeled.
The separate system-route identity `isSystemTransaction || options == SkipValidation` is pinned so that exact
`SkipValidation` routing is not confused with the base helper. Within the admitted helper, that Boolean disables
only nonce and block-limit checks.

## Reproduce

From this directory, run:

```powershell
.\Verify.ps1
```

The script regenerates into `D:\tmp\formal-verify` (or the supplied temporary root), byte-compares IR, manifest,
and Lean with `Generated/`, runs the C# mutation/artifact tests, and builds the Lean reference, vectors, generated
kernel, and refinement. All .NET builds use `-p:SaveDiskSpace=true` and warnings as errors.

The checked-in generated hashes in the current source snapshot are:

| artifact | SHA-256 |
| --- | --- |
| `Generated/OrdinaryStaticAdmissionKernel.ir.json` | `371b62d91c38bfeee01f068d8f1a120b9e2a54673dd36368e104a7d9a88ed94c` |
| `Generated/OrdinaryStaticAdmissionKernel.source-manifest.json` | `4051605bd7fdb7c86dd689c1739b896f6c9ed2440565e513aaac12f3ba29b92c` |
| `Generated/OrdinaryStaticAdmissionKernel.lean` | `fe1f879297f3370edd0e6da2fcca8c4030bbd9bbeb67765f7ca7ec3fe1b8dfad` |

These hashes must be refreshed if the admitted source or source-lowering implementation changes. The manifest also
pins the canonical initializer IR, manifest, generated Lean, refinement, and mathematical `TransactionGas` target;
the canonical manifest's production-source identity is checked against the admitted C# bytes.
