# Aggregate readiness audit

Date: 2026-09-22  
Audited HEAD: `1555fe67b016745fdfb8ecd53bba7fe26d691679`  
Reference log: `D:/tmp/formal-verify/formal-aggregate-after-ordinary-completion.log`

The audit below records the initial read-only finding at the stated HEAD. The dated
pre-root remediation checkpoint and subsequent Root2 resolution preserve the evidence sequence.

## Pre-Root2 remediation checkpoint (2026-09-22)

The deterministic build profile now pins the manifest revision
`b2478235e71e6a7ec2a509aa0155e25d5fdfff80` and epoch `1789035784`.
The compiler-input and source-pin cascade was materialized without a production-source change.
The first resumed static rereview found stale exact schema constants in finite/publication and
refund/completion; those constants were updated from current generated and compiler inputs.
Refund and completion live extraction/check pass. Finite and publication live extraction/check
pass after the Branch-to-finite-to-publication repin. The refreshed Branch, Outer, Refund,
and Completion focused gates passed in that order. One fresh root `Verify.ps1` run then stopped
at the synchronous-pipeline composition byte pin for `BlockProcessorExtractor/Extractor.cs`.
That extractor's only source diff is `JsonSerializerOptions.NewLine = "\n"`, preserving
deterministic LF output without changing the admitted block-control semantics. The legacy
`BlockProcessorControl` triplet was regenerated; its IR has the same normalized content, and
its Lean projection changes only the IR-hash comment. The synchronous composition pins now
identify the current extractor and generated Lean bytes. Its `SOURCE_PINS.json` SHA-256 is
`1e1cdbaebf17f3d6eb363536f03e213dd4cbe9fa2fe04efd6e57c08f76906437`.
At that checkpoint, no second root result was claimed yet.

Current checked-in triplets (IR / source manifest / generated Lean):

- Branch: `c515fdae0951e324314529f88779f808012fc4edf05df465b439d9efc85c66f8` /
  `eb8970d04da93072683870aa4b619cee1dc28c4dbf572fd88afe3d48c2ca24a3` /
  `c452d754043b7bd79a91cdf9312f56f307acac5975557fa3726385aab817bee9`.
- Finite: `5cabd18b8004f0be0408245ed1c2ce1e0c7d4e8e4bab9bf65c4ef0f5519c3f94` /
  `82af4f511d8a263ddb499e2544b70d72eee2448128439fd481156475fcd9c218` /
  `62d7c8b7c8500f87b308398d62f47a3a40e095c66e192bf654b09559dca13c62`.
- Publication: `1021965fb513138eed5f199da3ec297cad289d4709bb0a869024ec6169e41b38` /
  `6f889d7b174ef1b700edacbf761dc66a063177e45c0afde7290d89bfe2e80356` /
  `c6a1582dca38c82e876ab381276db1dcb18d4fdab83e0cf478f29c641d0395b9`.
- Refund: `b198837b7f2aaddd2f3d7c8653532d7a0ebc58dfd9566e6d7459121a0c051527` /
  `a0dd398a94994b65c3ea6bc59b6d62cd77958025208c423237e14a44d1ce30a1` /
  `367d0e3b71860d2bdb572cc0e9e7fd1951e02625b64200650e35e5085ca7b059`.
- Completion: `c21cb971b85a24115eebe54cf5b4def18b3b6ff049752a427363b952aae02292` /
  `a760ee5916cca8bc064d7bdbb5ff5e22aee1b5f8ba1f606c2143156036581081` /
  `ea61c5d977ad0d9a5fa62e4a44ed834588fa6daca943da2f4e01c20b19c86e97`.

The global manifest is `6288de7b5e374feac655bd67fd05f178117c285c3070adfc1a46f0a8539952fc`;
the processing-coverage inventory is `80e79b767f5e265c7221b0aada42bfd846942030376ea9577165012331c3f7f5`
and matches a fresh generator comparison. The global claim remains incomplete.

## Root2 resolution (2026-09-23)

The composition-pin repair passed independent rereview and the synchronous static audit,
preparation-boundary regression, deliberate operational-refusal check, and direct legacy
`BlockProcessorControl` byte comparison. The sole second fresh aggregate invocation,
`tools/Evm/Lean/Verify.ps1`, exited 0 with final `Verification passed.` in
`D:/tmp/formal-verify/resume-root2-20260922.log`. Its 58 reported test batches totalled
8,602 cases: 8,599 succeeded, three were explicitly skipped, and none failed. The skips
were the parallel-only storage-cache case, the maximum-round BLAKE2F case, and the
not-yet-implemented future-losing-branch scenario. The root passed the nested Receipt,
Fold, Finalization, ProcessOne publication, Branch, Outer, Refund, and Completion gates;
Completion finished with all 13 generated semantic mutations rejected and a 42-module,
13,336-declaration standard-only census with 17 negative controls.

Post-run live identities still match the checked-in triplets and hashes above, including
the global manifest `6288de7b5e374feac655bd67fd05f178117c285c3070adfc1a46f0a8539952fc`
and processing coverage `80e79b767f5e265c7221b0aada42bfd846942030376ea9577165012331c3f7f5`.
The manifest's status prose is intentionally left conservative; its exact verified bytes
are unchanged by this documentation-only resolution. The formal claim remains incomplete:
a green aggregate gate does not discharge the explicit runtime, external-hook, VM and
production-composition premises of the conditional artifacts.

## Initial result at audited HEAD

The committed accepted artifacts are not ready for a fresh aggregate verification run. Two deterministic metadata problems must be corrected first. Neither is a production Nethermind consensus bug.

1. Accepted compiler closures are tied to the Git SHA that happened to be `HEAD` while the artifacts were generated.
2. The published self-hash of the synchronous block-pipeline source-pin file is stale.

The reference log does not close this gap. Its last complete root attempt is `AGGREGATE CLEAN ATTEMPT 2 EXIT 1` at line 4286. Later entries establish focused frame, metadata-cascade, package, documentation, and live checks, but the log contains no subsequent root `Verification passed.` result. It was last written on 2026-09-17, before the audited commit dated 2026-09-18.

## Blocker 1: compiler inputs encode the pre-commit Git SHA

The accepted refund and ordinary-completion source closures include this compiler-generated source:

`src/Nethermind/artifacts/obj/Nethermind.Evm/release/Nethermind.Evm.AssemblyInfo.cs`

Both accepted source manifests pin it as:

`1b09e0a9a99a509f7d03a928cc5c2d010b58d81ba1fc8d7bf68f7f08c5f8c52a`

The current file hashes to:

`766ce4981e52ae6d7d2a7c062519625465a9eb33296fe6d0998a4dcea480305c`

The only relevant identity change is the generated `AssemblyInformationalVersion`: the accepted hash is reproduced when the current `+1555fe67b016745fdfb8ecd53bba7fe26d691679` suffix is replaced by `+b2478235e71e6a7ec2a509aa0155e25d5fdfff80`. The affected accepted manifests are:

- `OrdinaryTransactionRefundAdapterExtractor/Generated/OrdinaryTransactionRefund.source-manifest.json`
- `OrdinaryEvmCompletionExtractor/Generated/OrdinaryEvmCompletion.source-manifest.json`

The same coupling affects compiler-reference inventories. A current-file comparison found:

- `EvmTransactionPreparationExtractor/COMPILER_REFERENCE_PINS.json`: 49 of 139 repository-local references differ; the other 90 local entries are third-party binaries and still match.
- `ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json`: the same 49 of 139 repository-local references differ.
- `OrdinaryTransactionRefundAdapterExtractor/Admission/COMPILER_REFERENCE_PINS.json`: all 9 repository-local references differ.

Those inventories flow into the accepted preparation, receipt-terminal, branch-accepted-iteration, refund, and ordinary-completion artifact families. The root verifier builds `tools/Evm/Evm.slnx` first, then invokes the receipt, branch, refund, and completion package checks. A current-HEAD build therefore regenerates first-party assemblies with `+1555fe67...`, while the accepted inventories require the pre-commit `+b247823...` bytes and MVIDs. The root run will fail closed before it can establish aggregate success.

### Recommended correction

Use the manifest's pinned `pins.nethermindCommit` as the verification build's MSBuild `SourceRevisionId`, while retaining the pinned `SOURCE_DATE_EPOCH`. Do not merely repin artifacts after each new commit: that preserves the self-invalidating cycle.

The smallest fail-closed design is:

- In `Verify.ps1`, after validating `pins.nethermindCommit`, set the process `SourceRevisionId` to that value before any build and restore the prior value in `finally`.
- Make standalone package gates do the same in:
  - `EvmTransactionPreparationExtractor/Verify.ps1`
  - `ReceiptTerminalFoldExtractor/Verify.ps1`
  - `OrdinaryTransactionRefundAdapterExtractor/Verify.ps1`
  - `OrdinaryEvmCompletionExtractor/Verify-Candidate.ps1`
  - `BlockProcessorExtractor/Verify-Branch.ps1`
  - `BlockProcessorExtractor/Verify-Outer.ps1`
- Explicitly pass the pinned revision into the nested MSBuild source-resolution process in:
  - `OrdinaryTransactionRefundAdapterExtractor/CompilerReferences.cs`
  - `OrdinaryEvmCompletionExtractor/CompilerReferences.cs`

The explicit nested-process property matters: it keeps the source gate deterministic if its wrapper is bypassed. Pinning `SourceRevisionId` removes only repository-label churn. Raw production sources, selected compile paths, compiler references, assembly bytes, MVIDs, source syntax, and all semantic admissions remain exact and fail closed.

After that change, regenerate and independently recheck the affected cascades once. This includes the three compiler inventories, the two AssemblyInfo-bearing source closures, preparation/receipt/branch downstream artifacts, refund/completion artifacts, schemas that freeze those identities, the global manifest, and both coverage inventories.

### Required regression checks

- A build-input test must override or vary the ambient/current Git revision and show that the generated `AssemblyInformationalVersion`, the three compiler inventories, and the two source closures remain bound to the manifest pin.
- A negative control must alter a first-party source or compiler-reference byte and show that the existing exact hash/MVID gate still rejects it.
- Refund admission and ordinary-completion baselines must continue to assert their complete source/reference rosters; the revision fix must not silently remove compiler inputs.
- A root-level post-build check should verify the pinned revision in the generated AssemblyInfo before any accepted extractor check runs.

An alternative is to exclude or normalize only the generated informational-version attribute. That is more invasive because the current gates admit the complete MSBuild compile roster and exact compiler-reference bytes. A pinned `SourceRevisionId` preserves that existing model with a smaller semantic change.

## Blocker 2: stale synchronous-pipeline source-pin self-hash

The current raw SHA-256 of:

`SynchronousBlockPipelineMachineExtractor/SOURCE_PINS.json`

is:

`6c50687d71c4217ad181fa532e2ef4c6a4f4e0474cfbd76aa9678dc2f6059af3`

The following accepted documentation/global-status surfaces still claim:

`4abb555b5bb6f9ca29b5507ee6d39ec9f8ee582d844bee17d47691f27c90c656`

- `README.md`, line 73 at the audited HEAD
- `VERIFICATION_SCOPE.md`, line 33 at the audited HEAD
- `verification-manifest.json`, the `coverage.synchronousBlockPipelineMachine` status text

At the audited HEAD, all 65 individual paths and hashes inside `SOURCE_PINS.json` matched their files. `METADATA_MIRROR.json` also matched its published hash, `fb997fb68896341b2d32709c30d44fe91e0f503a6a5d9c1debe6ca472046a0fc`. The defect then was the published self-hash, not a stale production pin.

The synchronous package is otherwise correctly static-only: the root runs `--audit-pins`, runs `Test/PreparationBoundary.Tests.ps1`, requires operational `--extract` to fail with the exact refusal message, and rejects creation of the forbidden generated artifact. No operational refinement is admitted.

## Checks that are already reconciled

- The working tree was clean at the audited HEAD.
- All 30 accepted artifact files pinned by the global manifest's ten accepted triplets exist and match their recorded hashes.
- The current ordinary-completion triplet is consistently recorded:
  - IR: `7b8b6b3ae5964c1e4c0740550e7d18756473529834cdaf60027fcb3c7de81601`
  - source manifest: `c46d9bafd3b48294b41a457ddc242aa0f235fb4969da9d6bcb3f622558f76c4f`
  - Lean: `5865a04c4acb8322a3ee7dc9c3638287bec1f7a5ac74069a71562d79df13df98`
  - declaration inventory: 42 modules, 13,336 declarations, SHA-256 `103c5bb4aa37510df26581e0ca0fd3b329a7ca496ea467f464c30f08419767bd`
- The seven TransactionProcessor-dependent families have current raw production-source pins, except for the deliberate compiler-generated AssemblyInfo problem described above. Their ordinary raw source comparison found no other accepted mismatch.
- Frame/precompile Stages A through E have current triplets and dependency identities. Stage E's root test filter excludes `Operational_` Stage F tests, compares only accepted Stage E artifacts, and builds/checks only the five accepted Stage E modules.
- Every checked literal project, target, script, and artifact path used by the root verifier exists. `dotnet`, `lake`, and `pwsh` are available at the audited environment paths.
- `standard-mainnet-processing-coverage.json` pins the current global manifest hash, `b4a1c086591ecb14845005c008093c0d61ed60e4d303fdfbc6de07394625988d`.

These static checks do not substitute for a new serialized root run after both blockers are corrected.

## Draft residue, not accepted blockers

The unaccepted `OrdinaryPostNonceDispatchExtractor` and `SimpleTransferCompletionExtractor` source manifests still pin `TransactionProcessor.cs` at `f873146ec6184b942b9fb486adeab1d1141e4c216fb576e2e59041f520f731ff`; the current source is `0374c6f35a9a23361f37b41162ac281ca83b09846ab4295d5a592db6315c99cc`. The post-nonce compiler inventory is also subject to the Git-revision binary churn above. These packages are explicitly unaccepted candidates/drafts and must not be confused with the accepted root-gate blockers.

Stage F operational frame work and operational synchronous block extraction also remain explicitly unaccepted/refused. Their absence is not an aggregate defect.

## Production-bug classification

This audit found no new production Nethermind bug. The two accepted blockers are verification-harness and published-metadata defects. Previously reported production corrections and trace issues are outside this readiness audit and are not reclassified here.
