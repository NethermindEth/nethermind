# Transaction processor lifecycle extraction

This package admits a bounded control-flow slice of Nethermind's ordinary standard-mainnet transaction processor. It does not claim that transaction execution, the EVM, world state, cryptography, dependency injection, or the CLR has been formally verified.

The extractor reads and hashes four production sources:

- `TransactionProcessor.cs` for ordinary transaction admission, dispatch, tail ordering, rollback/deployment ordering, and final result/receipt projection.
- `ExecutionOptions.cs` for the execution-mode flag values used by those decisions.
- `EthereumGasPolicy.cs` for the standard gas-policy type binding.
- `BlockProcessingModule.cs` for the first unconditional `ITransactionProcessor` registration.

It validates the direct source path

```text
BlockProcessingModule.AddScoped<ITransactionProcessor, EthereumTransactionProcessor>
  -> EthereumTransactionProcessor
  -> EthereumTransactionProcessorBase
  -> TransactionProcessorBase<EthereumGasPolicy>
```

The generated semantic IR and strict source manifest bind that path to exact SHA-256 source identities, path-qualified complete-source admissions, build metadata, artifact hashes, and semantic bindings. The generated Lean file contains definitions only. `Refinement/TransactionProcessorLifecycle.lean` independently restates the admitted behavior and proves that the generated prefix and finalization projections agree with that specification and with the accepted `TransactionReference` event order.

Anchor discovery walks only the executable body of each admitted method: invocations inside local functions and lambdas cannot satisfy an anchor. Normalized Roslyn token fingerprints additionally bind all six admitted lifecycle methods, so an otherwise-unmodeled branch or early return fails closed even when the named anchors remain in source order. Their identities are source-path, namespace, generic owner and method/arity qualified; collisions are rejected before emission.

External hooks are explicit in the IR. In particular, sender recovery, validations, arithmetic, account and storage behavior, VM execution, code deployment, rollback/commit correctness, refunds and fees, receipt bytes, tracing, the registration DSL, Autofac resolution, and CLR/JIT behavior are not proved here.

## Artifacts

- `Generated/TransactionProcessorLifecycle.ir.json` — deterministic semantic IR.
- `Generated/TransactionProcessorLifecycle.source-manifest.json` — source and artifact identities.
- `Generated/TransactionProcessorLifecycle.lean` — theorem-free extracted definitions.
- `Refinement/TransactionProcessorLifecycle.lean` — independent handwritten specification and refinement theorems.

## Verification

From this directory:

```powershell
dotnet build .\TransactionProcessorExtractor.csproj -c Release -warnaserror -p:SaveDiskSpace=true
dotnet build .\Test\TransactionProcessorExtractor.Test.csproj -c Release -warnaserror -p:SaveDiskSpace=true
dotnet run --project .\Test\TransactionProcessorExtractor.Test.csproj -c Release --no-build -- --minimum-expected-tests 25 --no-ansi --progress off
lake build TransactionProcessorExtractor.Refinement.TransactionProcessorLifecycle
lake env lean -DwarningAsError=true ..\TransactionProcessorExtractor\Generated\TransactionProcessorLifecycle.lean
lake env lean -DwarningAsError=true ..\TransactionProcessorExtractor\Refinement\TransactionProcessorLifecycle.lean
```

The NUnit suite compares fresh extraction byte-for-byte with the checked artifacts and mutates every admitted control family to ensure drift is rejected. Re-run the extractor after an intentional production-source change, then review the semantic IR delta before accepting regenerated artifacts.
