# EnvironmentOpcodeExtractor

This fail-closed Roslyn extractor connects the 20 Amsterdam environment/context
opcodes and their 80 standard dispatch specializations to the independent
`Eip803x.Evm.Environment` and `EnvironmentStack` specification. The package-wide
`closed_amsterdam_refines` theorem covers the gas/PC/stack/status projection for
all four dispatch tables. See `SCOPE.md` for its exact trust boundary.

From the repository root, generate fresh artifacts with:

```powershell
dotnet run --project tools/Evm/Lean/EnvironmentOpcodeExtractor/EnvironmentOpcodeExtractor.csproj -c Release -- --repo-root . --output tools/Evm/Lean/EnvironmentOpcodeExtractor/Generated
```

The checked IR, source manifest, and generated Lean must be byte-identical to a
fresh run. Build the root Lean project after generation so both the independent
generated model and `Eip803x.Refinement.EnvironmentOpcode` are checked.
