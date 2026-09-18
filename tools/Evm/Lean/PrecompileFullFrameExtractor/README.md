# Full-frame precompile adapter: Stage C

This isolated package admits the standard-mainnet Amsterdam production route from an already-created precompile `VmState` through its top-level substate or nested parent-resume token. It does not complete the EVM frame driver.

The admitted route is `ExecuteTransaction → ExecutePrecompile → RunPrecompile<Eip158> → ExecutePrecompileCall → settlement`. The direct STATICCALL helper remains in the separate Stage B package. Action tracing uses `VmState.To = CodeSource ?? ExecutingAccount`; account balance touch and the RIPEMD latch use `Env.ExecutingAccount`, preserving CALLCODE/DELEGATECALL address semantics.

`Admission.json` is the reviewed exact-byte source and dependency profile embedded in the extractor. Roslyn additionally parses the production files, requires the named member multiplicities and records member and ordered statement hashes. Any insertion, reordering, expression change, directive change, missing member, altered dependency, path case change, reparse point or admission-file change fails closed. A source update requires deliberate review of the profile and the emitted operational template. This is an exact-profile lowering, not a general C# compiler or a proof of Roslyn/CLR semantics.

The deterministic output is a typed IR, source manifest and theorem-free Lean transition. The generated transition follows execution order. The independent reference selects a decision before projecting state and uses `EvmFrameMachineExecution.settleChild` only as its settlement target. It does not import or call the emitted kernel. `Entry.Admitted` states the pre-execution frame boundary; a proved preservation lemma derives the raw settlement domain from generated execution, instead of accepting that post-execution fact from the caller. The refinement theorem quantifies over inputs, leaf oracles and explicit front/settlement adapter premises, including preservation of the parent stack by front callbacks.

The accepted Stage A triple and all 18 oracle files are exact-hash pinned. Extraction verifies the entire Stage A production and dependency closure remains current. Pricing, state-gas refund/restore/advanced-refund/repayment, FrameJournal transition/finite trace and CallCreate closure theorem identities are pinned separately. Their presence is dependency admission; concrete world/gas adapter agreement remains a stated premise, not a discharged WorldJournal or transaction-composition theorem.

Run from the repository root, serially with other .NET/Lean work:

```powershell
dotnet build tools/Evm/Lean/PrecompileFullFrameExtractor/PrecompileFullFrameExtractor.csproj -c Release -p:SaveDiskSpace=true -warnaserror
dotnet run --project tools/Evm/Lean/PrecompileFullFrameExtractor/PrecompileFullFrameExtractor.csproj -c Release --no-build -- extract . tools/Evm/Lean/PrecompileFullFrameExtractor/Generated
dotnet test --project tools/Evm/Lean/PrecompileFullFrameExtractor/Test/PrecompileFullFrameExtractor.Test.csproj -c Release -- --filter FullyQualifiedName~PrecompileFullFrameExtractorTests
```

Run `lake build -KwarningAsError=true` inside this package. `Verify.ps1` performs the warning-as-error build, mutation suite, two independent emissions with byte comparison, checked-in artifact validation and Lean gates.

See [SCOPE.md](SCOPE.md) and [TEST_PLAN.md](TEST_PLAN.md) for the branch contract and remaining assumptions.
