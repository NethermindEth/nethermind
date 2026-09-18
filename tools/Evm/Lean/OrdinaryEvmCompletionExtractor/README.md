# Ordinary EVM completion — conditional continuation refinement

This package designs the first ordinary transaction composition slice from a **computed final preparation result**, through an externally observed VM return, to refund, processor counters, fees, commit and the sequential receipt terminal.

Status: **independently accepted conditional source-audited refinement** (schema 1, extractor 1.0.0; artifact state `accepted-conditional-source-audited-refinement`). Both whole-package candidate reviews passed, and final independent reconciliation recovered the exact earlier candidate artifacts by reversing only promotion metadata. The repeated full gate, shared solution build and all four upstream/completion postchecks pass. A production differential adapter remains open. The global verification claim remains incomplete.

Read [DESIGN.md](DESIGN.md), [SCOPE.md](SCOPE.md), [SOURCE_AUDIT.md](SOURCE_AUDIT.md) and [TEST_PLAN.md](TEST_PLAN.md). The independent Lean specification and stage refinement are in `Specification/` and `Refinement/`; the source-attached bridge relates the restricted generated continuation to that specification without upgrading preparation's model-to-model boundary.

`source-map.json` pins the bounded production behavior graph, including selection inputs; it is **not** a complete Roslyn compilation closure. `upstream-artifacts.json` pins the three accepted artifact triplets. `stage-dependencies.json` pins the three central theorem names, Lean toolchain and 46 dependencies: all 37 repository Lean files in their textual import closure plus nine accepted axiom-audit scripts/inventories. Their schemas freeze the exact ordered rosters and reject extra, missing, renamed or repinned entries. These immutable identity-map snapshots retain their original static-draft labels; the generated source manifest records the current package state. The source-file map does not replace the separate compiler-discovered declaration/axiom census.

The C# command `--audit <repository-root>` checks the embedded identities. `--source-audit` additionally resolves the real Release/non-zkEVM MSBuild compilation: 157 pinned source/selection identities, 146 compile trees and 226 selected metadata references with exact bytes, assembly identities and MVIDs. It binds ten fully qualified roots and 43 typed continuation sites, records CFG blocks/regions/capture identities, and rejects branch, target, argument, receiver, conversion, callable and source drift. The inherited compiler inventory's source roles describe that upstream inventory, not a new classification of this slice's ten roots.

`--extract <repository-root> <output-directory>` emits the conditional continuation after a fresh source audit. `--check` recomputes and compares all three artifacts byte-for-byte. The writer atomically replaces each complete file and publishes the manifest last; it does not claim a multi-file filesystem transaction. The exact ordered 131 dependencies include this package's proofs, template, schemas, gate scripts and declaration inventory as well as the pinned upstream boundaries. Runtime discovery requires every own C# source, including test sources, to appear in the inventory. The solution includes the extractor and its tests; the root gate invokes the complete nonrecursive package gate once.

The authored specification/proofs remain unchanged. An independently reviewed metadata-only refresh updates imported stateful-prefix and lifecycle Lean identities after their source-pin refresh; the generated evaluator changes only its closure/IR identity strings. The complete package gate passed again against these artifacts. Evidence: `D:/tmp/formal-verify/formal-aggregate-after-ordinary-completion.log`. This refresh does not broaden the accepted boundary.

## Verification evidence

The complete promoted package gate passes: standalone C# build with warnings as errors; all 46 tests without skips; exact source/artifact checks; 30 identity-schema and 866 strict artifact-schema negative controls; the exact generated/refinement/vector Lean targets (43 jobs); 13 compile-valid generated semantic mutants rejected by concrete equalities; and the complete declaration/axiom census with 17 controls. The promoted extraction runs twice with byte-identical outputs. `Evm.slnx` Release builds with zero warnings/errors, and live completion/preparation/refund/receipt postchecks pass under the pinned build epoch. The source identity auditor checks 41 behavior-map files, nine upstream artifacts and 46 stage dependencies.

The exact Spec/Refinement/Vectors build passes with warnings as errors (41 jobs), as do all three direct file checks: ten authored specification, 35 stage/refinement and 50 vector theorems. Four constructive internal-domain examples cover success, REVERT, exceptional halt and a non-CREATE dead-recipient state charge; full domains remain conditional on actual external provenance, not arbitrary true predicates. Nine finite wrong semantic alternatives are detected by named inequality theorems. These are not generated-artifact mutation gates.

Receipt accounting bounds are derived from existing counter-sum bounds. The generated bridge has 20 private component lemmas and three public theorems; its exact target and direct file check pass with warnings as errors. A focused diagnostic found 52 theorem declarations owned by that compiled module, including private and lazily generated local equations. The complete frozen candidate census separately covers 42 repository modules and all 13,336 declarations (12,791 public, 545 private), not only theorems. It checks transitive standard-only axioms and rejects opaque declarations, missing/unexpected modules/exports, removed private/equation declarations, indented descendants, newly materialized local equations and forbidden dependencies. All 17 controls pass. Inventory SHA-256: `103c5bb4aa37510df26581e0ca0fd3b329a7ca496ea467f464c30f08419767bd`.

The executable C# run passed 46 tests without failures or skips: 28 compile-valid production-source mutations rejected by named semantic guards, four duplicate-field/atomic-replacement controls, one deterministic renderer check, and 13 own-source completeness/drift controls. The disposal-order mutant actually moves refund inside the frame scope. The artifact review caught the initially omitted `OperationLowering.cs` dependency; the 131-file roster and regressions correct that verifier defect. The earlier 130-dependency full run is superseded. No production differential execution is claimed.

Unchanged specification/proof source hashes (SHA-256):

- Specification: `aca4b9b62a644ded54f4e0afd791cbbf1b9fa5ad9ac7a640e677008f3de0a235`.
- Refinement: `4d780ba0cd871e0e9312233c59122b343a32d5c67f1c1f5b400050082996d9bf`.
- Vectors: `629d1311acc8ea56b9259b867b5b4fe580f656b5038ef15ad0743839fa8fb5f1`.
- Conditional source-attached bridge: `34eaba4b1f1b51790480972d222d1b4fac6494892065888c3343fef8ede06012`.

Accepted artifact hashes (SHA-256):

- IR: `7b8b6b3ae5964c1e4c0740550e7d18756473529834cdaf60027fcb3c7de81601`.
- Source manifest: `c46d9bafd3b48294b41a457ddc242aa0f235fb4969da9d6bcb3f622558f76c4f`.
- Generated Lean: `5865a04c4acb8322a3ee7dc9c3638287bec1f7a5ac74069a71562d79df13df98`.

Run `pwsh -File tools/Evm/Lean/OrdinaryEvmCompletionExtractor/Verify-Candidate.ps1` from the repository root. The historical script name is retained; it is the complete package gate, not a reduced draft check. A green gate does not discharge external VM/hook provenance, signed-gas/no-wrap conditions or preparation's model-to-production correspondence gap. Fresh aggregate root verification remains a separate milestone.
