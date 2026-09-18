# Verification plan

The package has static checks for the exact source/IR boundary and Lean vectors for the fold
control surface. Execute the commands below only when the serialized .NET/Lake lane is available.

## .NET extractor and tests

```powershell
dotnet build SequentialBlockTransactionFoldExtractor.csproj -c Release -p:SaveDiskSpace=true -warnaserror
dotnet build Test/SequentialBlockTransactionFoldExtractor.Test.csproj -c Release -p:SaveDiskSpace=true -warnaserror
dotnet test --project Test/SequentialBlockTransactionFoldExtractor.Test.csproj -c Release --no-build -- --minimum-expected-tests 314
```

The suite defines 314 C# cases. Both fixtures first require an admitted unmutated
baseline; source-semantic mutations separately require successful complete compilation,
then the exact named admission diagnostic and an empty output directory. Invalid compilations
cannot count as semantic mutation successes. Eighteen diagnostic controls inject errors outside
admitted auxiliary and support members to test complete compilation; four controls change the
MSBuild alias target, name, condition, or multiplicity. Other tests cover:

- fresh unmutated and explicitly subscriber-free extractions reaching every auxiliary/route gate;
- distinct event evaluation and optional invocation blocks with null-skip and non-null/rejoin edges;
- six compile-first event controls for no-subscriber early return, subscriber-only commit,
  false-arm/local-function/lambda relocation, and a shadow receiver, plus seven typed-IR controls
  for event identity, optional-block confusion, branch swaps, missing edges, and a null-path loop;

- checked-in source/member/anchor/CFG scope;
- nine unused-lambda/local-function controls across main executor/caller anchors require the exact
  lexical-owner diagnostic; six paired throw-helper controls change the body, exception type,
  gas-limit block argument, or message and require the exact direct-throw diagnostic;
- sixteen iteration controls insert a conditional wrapper, `continue`, `break`, `return`, `while`/`do`
  repetition, an extra statement, or an outer conditional/repetition wrapper around the indexed
  loop, or add a gas-limit else arm containing a break, continue, return, repeated call, while/do
  repetition, or no statements; each requires the exact closed-body, direct-loop, or no-else diagnostic;
- six executor-CFG IR controls remove a guard edge, bypass the increment, repeat the helper call,
  change the back edge, or add an exit/block; each requires the exact iteration-topology diagnostic;
- two compile-first aliases insert implicit conversions through shadow transaction/result types,
  while retaining the admitted source tokens and helper signatures; nine typed-projection IR controls
  cover type/assembly identity, index/argument identity, conversions/operators, a coordinated shadow,
  and missing evidence;
- three compile-first async-void controls cover `ProcessTransaction` and both exception helpers;
  two further controls replace `ProcessTransaction` with an extern bodyless declaration or a deferred
  iterator method. Eighteen IR controls
  cover async/iterator/partial/extern/abstract/bodyless variants for all three callables, and two
  inventory controls omit or duplicate a callable;
- nine compile-first attribute controls add `Conditional`, its alias, or `MethodImpl(Synchronized)`
  to each callable; fifteen IR controls change conditional status or omit, duplicate, supply arguments
  to, or shadow the assembly of attribute evidence. Six strict JSON controls omit or duplicate the
  required conditional-status, override, or inherited-conditional properties. Every control requires
  its named diagnostic;
- seven compile-first lineage controls introduce an inherited conditional override, alias, deeper
  override chain, inherited `MethodImpl(Synchronized)`, plain override, base-only change, or extra
  interface. Thirty-three IR controls cover override/static/virtual flags, overridden methods,
  inherited conditional status and attributes, declaring type, base/interface closures, implemented
  interface methods, and missing lineage across all three callables;
- candidate, operation, symbol, position, source, canonical-anchor, auxiliary-operation-shape,
  auxiliary-CFG/owner, and commit-order IR mutations failing closed;
- a source mutation removing the exact post-commit anchor without partial artifacts;
- a source mutation that would otherwise rebind the post-fold anchor to the later excluded commit;
- a source mutation that changes the indexed loop condition;
- malformed compiler-reference path/hash, missing inventory, and duplicate assembly identities;
- source callback-order, adapter Start/Execute/End, tracer reset/append/index/end and
  Start/End delegate ordering, CFG early-return and local/lambda-owner relocation, direct receipt
  append, BAL true-arm return/else/no-op dispatch, BAL guard/Enabled derivation, decorator fallback,
  DI registration and registration order, exact-base signature/route, and non-virtual mutations;
  conditional/no-op gas-limit and false-result throws are rejected as non-direct guard bodies, and
  conditional callback placement is rejected by the normal-path CFG relation; ProcessBlock
  conditional StartNewBlockTrace/pre-commit and early returns between fold/callback/post-commit
  are rejected by the complete normal route, and gas/false-result/BAL true-arm bypass statements
  are rejected when the expected direct action is not the sole true-arm statement;
- empty input, nonzero initial index, invalid-first, malformed-first, false-result, receipt-index,
  and two-index vectors;
- production-source immutability during mutation extraction;
- theorem-free generated Lean and required terminal observation fields;
- byte-for-byte reproduction of the checked-in generated Lean by the emitter.

## Reproducibility

Run `Verify.ps1` to build, validate the checked-in artifacts, extract twice into isolated temporary
directories, compare both fresh runs to the checked-in IR/manifest/Lean bytes, and then run the Lean targets. `--check`
also re-emits into an isolated temporary directory and compares all three artifacts before accepting
the checked-in files. The extractor schema is bumped when auxiliary CFG evidence changes; the
checked-in IR/manifest/Lean must therefore be regenerated together once the receipt-terminal
manifest gate is refreshed. Publication uses per-file temporary siblings and replacement, so no
artifact is exposed before its complete bytes are written. The script sets and restores `SOURCE_DATE_EPOCH=1789035784`, runs `lake --wfail build` and direct Lean
warning-as-error checks, then runs `Verify-MutationGates.ps1` and `Verify-Axioms.ps1`. It must report
any source-pin, typed-binding, artifact, or theorem-placeholder failure before accepting the package.

## Lean targets

The direct targets are:

- `Generated/SequentialBlockTransactionFold.lean`
- `Specification/SequentialBlockTransactionFold.lean`
- `Refinement/SequentialBlockTransactionFold.lean`
- `Vectors/SequentialBlockTransactionFoldVectors.lean`

Sixteen named scenarios and the conditional refinement examples use kernel-checked `decide`
or structural proofs, never `native_decide`. They do not prove production supplied the terminal
observations. `OpenSourceCompositionObligation` remains unproved.

Ten Lean semantic mutations first compile the changed theorem-free kernel, then must fail the
proof/vector bundle at a semantic proof error; missing imports, typeclass failures, syntax errors,
and resource exhaustion do not count. The axiom gate checks a frozen 49-export inventory using
`Lean.collectAxioms`, permits only `propext`, `Classical.choice`, and `Quot.sound`, requires complete
unique audit output, and executes missing/duplicate/unknown/forbidden-axiom and export-completeness
negative controls.

Assignment anchors record and strictly compare ordered RHS calls, receiver symbols/types,
field/property references, and parameter owners/ordinals. Five additional compile-first controls
shadow the transaction-start receiver or introduce aliases for the current transaction, receipt
index, release spec, and suggested block. Exact property/Increment IR tests reject the former
field and combined-operation identities. The decorator mutation keeps unique base discovery and
must reach `fold.binding.di.parallel-decorator`.

The axiom census now traverses every descendant of the Specification/Refinement/Vectors roots,
including compiler-generated equation/helper theorems. Its 49 names comprise 33 explicit public
theorems and 16 compiler-generated descendants, with inventory SHA-256
`3f3de4d7350098b07bd112c51358dd249938f1b861c86e85bb7f02a0b71495d2`.
Any missing, duplicated, or unlisted descendant is a hard failure. Nested exported theorems and
forbidden axioms are separate negative controls. Private proof helpers are checked transitively
through the exported theorems that use them.
