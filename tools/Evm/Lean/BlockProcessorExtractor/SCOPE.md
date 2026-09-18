# Block processor source-event refinement

The separate [BranchAcceptedIteration extension](BRANCH_ACCEPTED_ITERATION.md) adds an
independently accepted conditional source-audited operational suffix. It does not change the
source-projection claims below. Its computed publication model retains supplied normal
ProcessBlock inputs, BAL-disabled routing, normal-hook, runtime-binding and noninterference
premises; it does not establish a whole-branch or complete-pipeline theorem.

The separate [finite-completion and outer-publication extensions](OUTER_BLOCK_COMPLETION.md)
add separately accepted conditional caller-control refinements for finite completion and
synchronous publication under their explicit source/input/normal-hook premises. They do not upgrade
the existing syntax-projection theorems or establish whole-block or persistence correctness.

This package extracts a bounded **source syntax projection** of the concrete
standard `BlockProcessor`, `BranchProcessor`, `BlockchainProcessor`, and
`BlockHeader` implementations. It does not prove execution-client correctness.

The four files named in `Extractor.Paths` supply the admitted current source.
Their exact SHA-256 identities in the generated source manifest are authoritative.
The independently reviewable `Admission/*.cs.txt` files are syntax allowlists,
not executable alternative implementations and not claims about a baseline commit.
Every selected method's complete token sequence, including signature, arguments,
receivers, guards, local functions, catches, finally blocks and return statements,
must match its allowlist. Each admission identity is source-path, namespace,
embedded-resource, owner and selector qualified, includes both template and
source-syntax fingerprints, and must be unique. Trivia may change; the source
hash still changes.
The checkpoint constant is also admitted and its production initializer supplies
the lowered modulus. Competing overloads in each
owner and competing selected-method declarations in the Processing/Core source
directories fail closed. Using aliases are rejected except for the existing exact
`BlockchainProcessor` metrics alias. Preprocessor directives in the four admitted
files are rejected.

This intentionally strict admission rejects harmless syntax changes as well as
semantic ones. Changing an admission template requires reviewing the source and
the new extracted IR; regeneration alone must not bless a template change.
The negative declaration scan is a local source-directory check, not a complete
MSBuild compilation closure or a C# semantic-model binding proof.

The extractor lowers actual admitted call/assignment nodes in source order,
records their source locations and enclosing `if`/`else`/`catch`/`finally`
contexts, and serializes those observations as JSON IR. The guard annotations
are lexical context, not complete reachability predicates: short-circuiting,
hook exceptions, and virtual calls still require the full admitted method and
external contracts. Lean output is emitted from a deserialized copy of that IR.
The JSON boundary is case-sensitive, rejects unknown members, malformed input,
omitted required constructor parameters, and top-level or nested nulls. Every
source-derived IR field is compared exactly after deserialization; the manifest
likewise round-trips and exactly binds source identities, qualified admissions,
template/source fingerprints and artifact hashes. No output is emitted after
source admission or serialized-lineage validation fails.

Generated Lean has its own phase, receipt-event and finalization enums. It
imports no handwritten reference and contains no theorems. The handwritten
refinement maps those generated types into `BlockReference` and
`BranchReference`; it interprets the source phase list with the reference's
explicit external contracts and proves agreement with its phase program.

The admitted observations cover:

- DAO before block preparation; block trace/context/BAL setup; beacon root;
  historical blockhash and pre-system commit; ordinary transaction execution
  and its post-transaction commit; receipt preparation; rewards; withdrawals
  and their commit; execution-request call; trace completion; storage/state
  roots; account changes; BAL finalization; header hash; processed validation.
- The synchronous receipt bloom/root computation and installation order.
  The complete background scheduling, await, and finally bodies are admitted,
  but the generated receipt-event projection selects the synchronous branch.
- `DisposeAccountChanges` after failed processing/validation, and branch scope
  disposal/reopening for retry, checkpoints and final cleanup.
- The successful per-block source order: `ProcessOne`, inclusion-list signal,
  prewarm wait, `PreCommitBlock`/`CommitTree`, then scope reset. CommitTree is
  called per successful block, including read-only mode; the 64-block condition
  controls scope reopening, not CommitTree frequency.
- `BlockHeader.CopyProcessingFields` and `PostValidation` assignment projections.
- Synchronous invalid-block classification and cleanup in the complete admitted
  `ProcessBranch`; total difficulty before optional head update before optional
  mark-processed in `BlockchainProcessor.Process`.

The Lean theorems establish source-to-reference phase ordering and interpreted
program equality, accepted-run trace order, inherited reference rollback,
synchronous receipt-event mapping, successful singleton commit position/count,
checkpoint equivalence for `index < total`, checkpoint edge examples, successful
finalization order after scope disposal, and exact source assignment lists.
These lists describe ordered source sites. Optional calls need not occur in every
runtime execution; finalization theorems carry explicit successful-hook premises.

The concrete runtime types, standard system-handler selection and synchronous
receipt choice are premises. DI resolution, virtual dispatch, full C# operational
semantics, CLR/JIT execution and integer/object representations are not proved.
DAO, beacon roots, historical blockhash, transactions, rewards, withdrawals,
execution-request internals, receipt/state/header hashing, BAL, tracers, callbacks,
scope implementation and persistence remain external. Header strings do not prove
constructor defaults, nullable fields, deep copy, widths or object-graph equality.
The request call and artifact assignment are anchored; request extraction/hash
semantics are not established here. Diagnostic replay and invalid-block deletion
are admitted source sites with external behavior.

In particular, reference rollback follows the supplied abstract scope contract.
Production cleanup source sites do not prove that `IWorldState` restores all
journals, that CommitTree preserves its abstract state token, or that writes survive
a crash. No production bug was confirmed while adding this slice.

From this directory, verify with:

```powershell
dotnet build Test/BlockProcessorExtractor.Test.csproj -c Release -warnaserror -p:SaveDiskSpace=true
dotnet run --project BlockProcessorExtractor.csproj -c Release --no-build -- --repo-root <repository-root> --output Generated
dotnet run --project Test/BlockProcessorExtractor.Test.csproj -c Release --no-build -- --minimum-expected-tests 43 --no-ansi --progress off
lake build BlockProcessorExtractor.Refinement.BlockProcessorControl
lake env lean -DwarningAsError=true Refinement/BlockProcessorControl.lean
```

The 43 focused NUnit cases cover checked-artifact drift and determinism, generated
independence/theorem absence, trivia versus source identity, competing partial
declarations, strict IR/manifest parsing and exact field lineage, admission-key
collision, and 34 source mutation cases including missing/reordered/duplicated calls,
wrong targets/arguments, aliases/escapes, hidden closures and early returns,
receipt/root/request/BAL/hash/cleanup corruption, commit placement, checkpoint
guards/constants, result classification and finalization changes. A paired
production/template mutation changes the checkpoint modulus to 128 and verifies
that this reviewed change reaches both IR and generated Lean. The internal test
entry accepts reviewed template overrides; the command-line tool always uses
its embedded admission templates.
