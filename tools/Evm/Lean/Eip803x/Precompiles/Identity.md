# Identity precompile leaf

`Identity.lean` models the pure standard-mainnet precompile registered at address `0x04`:
name `ID`, caching disabled, base cost 15, data cost 3 per ceiling-divided 32-byte word,
unconditional success, and byte-for-byte output equality. Its schedule is configurable.

`IdentityPrecompileKernel` is an allocation-free production pricing leaf used directly by
`IdentityPrecompile`. The restricted Roslyn profile extracts its base and per-word formulas to
`Generated/IdentityPrecompileKernel.lean`; `Refinement/IdentityPrecompile.lean` proves the generated
fixed-width formula equals the independent schedule whenever the input length is representable.
The checked adapter shape pins address `0x04`, name `ID`, disabled caching, exact pricing delegation,
and `inputData.ToArray()` as the execution result. Extractor mutation tests reject changes to every
constant, the ceiling formula, metadata, delegation, caching, and output expression. Production
tests exercise exact word boundaries, success, byte equality, and owned-copy behavior.

This closes only the pure pricing leaf and source-pins the copy adapter. It does not prove
`IPrecompile.TryConsumePrecompileGas`, the `VirtualMachine.RunPrecompile` account-touch and rollback
path, child-frame gas merging, allocation success, dispatch/registration, or the CLR semantics that
connect `ReadOnlyMemory<byte>.ToArray()` to a mathematical byte list. Those obligations remain
required before identity can be counted as a composed production precompile or in the whole-EVM
claim. The actual adapter supplies a nonnegative `Int32` length, which satisfies the theorem's
explicit `UInt32` representation premise.

Reproduce the leaf checks with:

```powershell
dotnet test --project tools/Evm/Lean/Extractor/Test/Extractor.Test.csproj -c Release `
  -p:TreatWarningsAsErrors=true -p:SaveDiskSpace=true -- `
  --filter FullyQualifiedName~IdentityPrecompileExtractorTests

lake build Eip803x.Refinement.IdentityPrecompile

dotnet test --project src/Nethermind/Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -c Release `
  -p:TreatWarningsAsErrors=true -p:SaveDiskSpace=true -- `
  --filter FullyQualifiedName~IdentityPrecompileTests
```
