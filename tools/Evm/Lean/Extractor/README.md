# State-gas and account-access Roslyn extractor

This fail-closed vertical slice binds selected production kernels from their pinned source paths. The `transaction-settlement` profile binds `TransactionSettlementKernel.Calculate` plus the exact block-execution projection and saturating-subtraction source definitions on which it depends. The `account-access-pricing` profile binds the pure `AccountAccessPricingKernel.Price` method and source-validates the exact `EthereumGasPolicy` adapter shape and ordering, including tracing warming, access tracking, precompile fact gathering, and delegated-access sequencing. It emits canonical semantic IR, importable theorem-free Lean transcriptions, and manifests containing source hashes, bound method signatures, compiler/extractor versions, and artifact hashes. It does not emit Lean proofs or axioms.

For the charge slice, the audit IR and Lean transcription are emitted from the same validated Roslyn semantic graph; its Lean emitter does not read the serialized JSON IR back. The transition slice first normalizes the validated graph to a canonical in-memory expression IR which drives both serialized IR and Lean emission. The extractor compiles each standalone kernel with pinned C# language/overflow settings and platform references, while the verification gate separately builds those sources through `Evm.slnx`. Loading the complete production MSBuild compilation and making serialized canonical IR the sole general code-generation input remain required before extending this mechanism to the whole EVM.

Run from the repository root:

```powershell
dotnet run --project tools/Evm/Lean/Extractor/Extractor.csproj -- `
  --repo-root . `
  --output tools/Evm/Lean/Extractor/Generated
```

Regenerate only the independently gated transaction-settlement profile with:

```powershell
dotnet run --project tools/Evm/Lean/Extractor/Extractor.csproj -- `
  --repo-root . `
  --output tools/Evm/Lean/Extractor/Generated `
  --profile transaction-settlement
```

Regenerate the account-access pricing profile with:

```powershell
dotnet run --project tools/Evm/Lean/Extractor/Extractor.csproj -- `
  --repo-root . `
  --output tools/Evm/Lean/Extractor/Generated `
  --profile account-access-pricing
```

The Lean modules are written to `tools/Evm/Lean/Eip803x/Generated/StateGasChargeKernel.lean`, `tools/Evm/Lean/Eip803x/Generated/StateGasTransitionKernel.lean`, and `tools/Evm/Lean/Eip803x/Generated/AccountAccessPricingKernel.lean`. Verification scripts pass profile-specific output paths so generated artifacts can be regenerated and byte-compared without modifying the checked-in artifacts.

All generated public definitions normalize their inputs to C# machine widths. Their bodies use explicit signed and unsigned 64-bit wrap and cast helpers for every unchecked arithmetic operation; bounded refinement hypotheses can therefore simplify the machine semantics without changing the generated definitions.

The accepted subset contains non-generic static value functions and readonly-struct constructors using primitive values, local variables, conditionals, returns, explicit record construction, local enum constants, parameter/local references, conversions, unary/binary operators, and the exact `Math.Min(long, long)` intrinsic. The only accepted method attribute is `MethodImplOptions.AggressiveInlining`. Integer checked/wrapping metadata is retained in the IR.

## Precompile gas-pricing reachability

The `precompile-gas-pricing` profile pins the standard policy adapter, both VM call
sites, `BlockProcessingModule`, and the production
`Nethermind.Core.ContainerBuilderExtensions` source.  It rejects directives,
aliases, static imports, and local shadows in the admitted DI source, then
semantically binds the selected `AddScoped<IVirtualMachine,
EthereumVirtualMachine>` invocation to the exact Core extension method and
assembly.  Same-namespace extension candidates are included in that constrained
binding compilation, so a competing `AddScoped` cannot satisfy a text-only
shape check.  This establishes the pinned module registration path only; it
does not prove plugin overrides, arbitrary child scopes, or the whole Autofac
composition graph.

Unsafe, async, exceptions, loops, mutable or external field access, virtual/interface calls, open generics, ref/out parameters, mutable object methods, unknown external calls, compiler warnings/errors, and every unlisted syntax or Roslyn operation are rejected. The settlement profile permits only the local assignments in its exact token-pinned method shape. Calls to source methods and constructors are followed transitively unless a profile pins and validates the complete source definition of a named intrinsic dependency; finding a similarly named token or hashing an unbound file is not success.
