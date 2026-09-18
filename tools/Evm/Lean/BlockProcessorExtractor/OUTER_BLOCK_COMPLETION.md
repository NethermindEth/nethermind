# Accepted finite branch completion and synchronous publication

`NormalFiniteBranchCompletion` and `BlockchainPublication` are separate operational
extensions. NormalFiniteBranchCompletion is independently accepted at schema 1 / extractor
1.0.0 after its complete executable gate and review. BlockchainPublication is separately
accepted at schema 1 / extractor 1.0.0 after its own executable gate and review. The existing
`BlockProcessorControl` syntax-projection theorems remain unchanged.

## Boundaries and composition

The finite boundary is the normal owned sequential branch after nonempty preloop
setup; the empty-list return is represented separately. Scope acquisition, initial
prewarming, branch selection and the initial events are entry obligations. Per-iteration
prelude completion is an external normal-return obligation, not a proof of task or
callback implementations. The entry observation records the actual suggested block,
threaded base header, first/subsequent spec-refresh guard and tracer-dependent options.
Resource handles describe the accepted-publication boundary; cancellation may already
be absent because the transaction-executed callback cleared it.

The generated finite runner starts with an all-empty array and zero prefix. It builds
each `BranchAcceptedIteration` input from the preceding computed slots, count and scope;
the next prewarm base is the preceding processed header. It invokes the upstream generated
runner and consumes that actual result. It does not accept an arbitrary completed array
or a caller-filled equality to the desired result. Inclusion false remains accepted;
`NoValidation` still writes true to both signals. Suggested-number CommitTree, count
increment only after normal commit, event/checkpoint/reset/hash-scheduling order and the
terminal nested-finally sequence are inherited through the upstream theorem.

The independent `NormalSteps` relation reconstructs each prefix from its own iteration
specification. Its refinement invokes the upstream source-attached theorem at every
constructed input. Recursive adapter premises are source identities, input-boundary and
normal-hook obligations, not output equalities. The final array is derived from the
computed slots. An internal invariant represents those slots as a filled processed prefix
followed by the remaining empty slots; it derives both terminal-iteration alignment and
complete final filling from initialization and each specified slot assignment. Neither
property is a caller-supplied adapter premise. This is a finite **caller-control** composition, not a finite EVM,
transaction or world-state execution theorem.

The publication boundary is `BlockchainProcessor.Process` after
`PrepareBlocksToProcess`, with the selected branch and options supplied as typed entry
observations. It invokes the finite generated runner; its independent theorem first
invokes the finite source-attached theorem and derives the returned array from the
independent finite relation. It then establishes this normal synchronous observation:

1. `ProcessBranch` returns its array and clears `error`; the stopwatch stops.
2. A nonempty array selects its last processed block. Its header receives the fresh
   nullable `suggestedBlock.TotalDifficulty`, not the earlier logging-local value
   coalesced with zero. The nullable UInt256 observation is bounded below `2^256`.
3. Read-write statistics use the processed array and branch base.
4. Unless `DoNotUpdateHead`, the tree receives the **suggested header**, `wereProcessed:
   true`, implicit `forceUpdateHeadBlock: false`, and the **prepared Blocks** span.
   A false return may log a warning and continues normally.
5. Independently, `MarkAsProcessed` calls the tree with **prepared Blocks**. It does not
   depend on the head-update guard or result and can run with `ReadOnlyChain`.
6. Read-write metrics precede return preparation. `ProcessingBranch.Dispose` releases
   **Blocks first, BlocksToProcess second** before the actual return.

`BlocksToProcess`, prepared `Blocks`, and returned processed blocks are distinct lists.
The first equals the finite suggested-input list, and its resource identity must equal
`finite.originalList`; the last comes only from the runner. The consumed, independently
specified preparation entry invariant requires forced preparation to have empty prepared
`Blocks` and exactly `[suggestedBlock]` as `BlocksToProcess`. Ordinary preparation requires
equal, nonempty prepared/input lists: the new input list is populated from the prepared
span, and normal return passes the source's `blocksToProcess[0]` access. An empty finite
branch is modeled independently but is not an admitted post-preparation publication entry.
Resource IDs, block/header identities and nullable observations are typed
tokens, not proofs of CLR heap representation. The shared options value is bounded to
the C# enum domain; masks are ReadOnlyChain `65`, DoNotUpdateHead `64`, MarkAsProcessed
`128`, NoValidation `8`, StoreReceipts `4`, and ForceSequentialBlockAccessList `512`.

## Failure classification is a separate boundary contract

The classifier does not assert that the normal finite runner throws. It describes an
observed skip or wrapper exception boundary, conditional on normal handler and required
outer-cleanup completion and stable input/hash observations. Only `Nethermind.Core.Exceptions.InvalidBlockException`
is caught by `ProcessBranch`; cancellation and other escapes remain unknown. Preparation
exceptions outside that wrapper are not silently reclassified as handled invalid blocks.

The handled-invalid observation preserves `ex.Message`, selects the first input block
whose hash equals `ex.InvalidBlock.Hash` for optional event evaluation, and records deletion
calls for **all** matching input blocks only when that nullable hash is nonnull and the
options are not read-only. Null hashes can match a block for event evaluation without
enabling deletion. Event evaluation does not assert that a subscriber existed. Diagnostic
replays, event/log callbacks, deletion internals and full exception unwinding are not proved;
the simplest external handler contract disables diagnostic replay and requires callbacks
to return normally without changing the modeled list/hash observations.

Failure observations distinguish unknown (`none`) from observed null/empty (`some none`
or `some []`). Generic escapes and a failed handler do not invent a cleared error, absence
of deletion calls, successful disposal or any final state. Normal-run hook escapes likewise
retain only an observed prefix, no returned block and no final state. Their stopped model
trace is not a claim that later C# finally blocks did not execute.

## Source admission and trust

The selected live owners are `Nethermind.Consensus.Processing.BranchProcessor` and
`Nethermind.Consensus.Processing.BlockchainProcessor`. Admission includes the complete
reviewed source token sequences, exact method parameter/ref-kind/return identities,
`Nethermind.Evm.Tracing.IBlockTracer`, `IBranchProcessor.Process`, `IBlockTree.TryUpdateMainChain`
and `IBlockTree.MarkChainAsProcessed` receivers/targets and the implicit false head argument.
Reduced extension methods are normalized through `ReducedFrom` when recording bindings.
The complete Process/ProcessBranch/PrepareBlocksToProcess/ProcessingBranch.Dispose CFGs
and the local DeleteInvalidBlocks CFG are recorded, together with 51 source anchors,
reachable block membership, guard context, symbols and statement dataflow.

The exact ordered source inventory has 23 members. The shared branch compiler lane
supplies its 13-member source closure and byte-pinned reference inventory; the outer lane
adds BlockchainProcessor, IBlockchainProcessor, IBlockProcessingQueue and BlockRef as real
source trees so every ProcessingOptions signature uses the same source type. Six other
pinned declaration/source files remain metadata references; their exact source bytes and
compiler metadata are both pinned. The four inherited compiler-support trees remain
separately bound by the accepted Branch manifest. This
is not an independently verified Roslyn, MSBuild or CLR semantics model.

Each IR has schema `1`, extractor `1.0.0`, exact operation/mask/source/dependency rosters and
six CFGs. The strict parser rejects malformed, duplicate/case-aliased, unknown, missing and
null fields, empty/whitespace targets and roster drift. The reader recomputes the ordered
identity digest. Separate per-slice IR and manifest schemas fix the exact source identities,
ordered dependency paths and output filenames, and exclude empty targets. The finite
schemas also freeze the accepted Branch artifact identities; publication's upstream hashes
freeze the independently accepted finite triplet.
Checked-in validation additionally compares every IR byte to
a fresh source audit, regenerated Lean and the strict artifact manifest.

Both extraction and checking run the **complete upstream validator before output**:
publication calls the finite checker, finite calls the branch checker, and branch calls
the settled ProcessOne/normal-tail/ReceiptTerminal validation chain. Direct dependency
identities are exact and ordered: 22 for finite completion and 21 for publication. Both
include their own template, independent specification, refinement and the shared outer
extractor implementation, plus slice-specific vectors, the frozen compiler export inventory
and census pin, strict schemas, promotion pins and all three verification scripts.
The publication closure deduplicates the already-bound outer extractor. Thus
changes to a slice's own proof or semantic inputs invalidate its checked-in lineage.
Neither an empty inventory nor a
`static-draft` upstream artifact is an acceptance witness. Source-only test audits deliberately
remain `static-draft`, with no accepted dependencies, and cannot be read as accepted or emitted.

Artifact writes use a unique same-directory temporary file, write-through plus an explicit
disk flush, and replacement only after the temporary file is closed. IR and Lean are
replaced before the manifest, which is the final publication marker. This is per-file
atomic replacement, not a transactional three-file commit; an interrupted mixed triplet
fails the existing manifest/byte checks. Failed replacements clean up their temporary file.

Trusted boundaries remain the extractor, compiler inputs/Roslyn, artifact checker, concrete
runtime implementation selection, hook normal return and noninterference, and typed external
observations. Unproved behavior includes ancestry/preprocessing choice, BAL retry, parallel
equivalence, unowned genesis, full scope acquisition, transaction/state composition, async
queue processing and recovery, cache-clear continuation completion, joined prefetch or completed
transaction hashes. Tree calls prove neither actual head change nor canonicality, trie
correctness, persistence, crash recovery, restart visibility or rollback. `TryUpdateMainChain`
returning true must not be read as a proof that the head changed. This is not a whole-block theorem.

## Separate finite and publication acceptance

The accepted BranchAcceptedIteration triplet is frozen in `FINITE_PROMOTION_PINS.json`.
The independently accepted finite triplet is:

- IR: `ebccc9751bc3f65dc577ad78e9202497b9eeb38d410ec43c0c5d8808e7a15816`.
- Source manifest: `5de2e5fe4d00d76f944806b5bf0044226011ead1f3a1a11ee99c80653dc01463`.
- Generated Lean: `71fb06de4352ff82041687051bf3fa72968fbd351a88068ced427f9011ba7638`.

The full finite gate passed 72/72 tests without failures or skips, both strict schemas and
185 negative controls, deterministic extraction, 31 warning-failing Lake jobs and four
direct Lean checks, four independently compile-valid semantic mutations, and all 2,317
standard-only descendant exports with negative/completeness controls under twenty-four
roots. The Release `Evm.slnx` build passed with zero warnings/errors; explicit live
ProcessOne, Branch, finite and refund postchecks passed. Evidence is in
`D:/tmp/formal-verify/finite-full-gate3.log`, `finite-final-evm-build.log` and
`finite-final-postchecks.log` under that same directory.

This accepted finite triplet is pinned in `PUBLICATION_PROMOTION_PINS.json`.
The separately accepted publication triplet is:

- IR: `38e3e21037eac7ac664e4aadf74d17111baf99ff4184ed78fa6984907b076d43`.
- Source manifest: `1f1f004187b2fa216a1f912f8a50cabba228500a74de4b73bd6176c200fa6291`.
- Generated Lean: `dc4889bf20e1a497b37a603b60c603ae61b1273c14f9c4618e617691276c2225`.

The complete combined gate passed 104/104 tests without failures or skips, 368 strict-schema
negative controls (185 finite and 183 publication), byte-identical regeneration, 35
warning-failing Lake jobs and eight direct Lean checks, all seventeen independently
compile-valid semantic mutations, and both complete standard-only descendant censuses
with their negative/completeness controls. The shared Release `Evm.slnx` build passed
with zero warnings/errors, followed by successful live ProcessOne, Branch, finite,
publication and refund checks with unchanged artifact identities. Independent executable
review accepted the exact triplet above. Evidence is in
`D:/tmp/formal-verify/outer-publication-full-gate1.log`,
`outer-publication-final-evm-build.log`, `outer-publication-final-postchecks2.log` and
`publication-independent-final-review.log` under that same directory. The overall
pipeline claim remains incomplete; these are conditional caller-control refinements.
All modes require `--repo-root` and `--output`.
No template markers or draft metadata should be hand-edited into accepted artifacts.

`Verify-Outer.ps1 -Slice finite` selects no publication artifact or proof. After finite
acceptance, `-Slice publication` gates the downstream slice; `-Slice all` repeats both.
Each selection requires full transitive source checks, two independently extracted
byte-identical triplets and exact schema negative controls. The expected test floors are
72 per slice and 104 combined, including forty shared cases. The 33 C# source
mutations require unmutated admission, a separate successful mutated compilation, and an
exact anchor-specific diagnostic before the complete-source fallback. Accepted-artifact
mutations require a real accepted baseline; stale upstream artifacts are not skipped or forged.

Ten scenario constructors execute the source-attached composition theorem: traced, one/two
blocks, inclusion false, skipped validation, read-only, mark without head, mark after false
head result, read-only plus mark, and forced empty preload. A separate empty-finite case
executes the finite theorem; outer empty, mismatched-resource, divergent ordinary lists,
forced nonempty preload and forced nonsingleton inputs are rejected. Additional observations cover
distinct suggested/processed TD identities, disposal order, unknown escapes and nullable-hash
first-match versus all-match invalid handling. The inherited branch vectors cover the 64-block
checkpoint boundary; the new finite vectors do not claim a separately executed 65/66-block case.

The 17 Lean mutation definitions (four finite and thirteen publication) independently compile each changed kernel before requiring
a semantic proof failure against unchanged specifications/refinements/vectors. A broken
baseline, import/type error, placeholder, recursion limit or heartbeat failure cannot count
as a killed semantic mutation. Standalone verification uses explicit `lake --wfail build`
targets, direct `-DwarningAsError=true` source checks, the mutation gate and a separate
per-slice axiom gate. The root verifier invokes `Verify-Outer.ps1 -Slice all`, covering
both accepted slices without invoking the root verifier recursively. Skip switches
report partial checks only.

The finite actual-module compiler census found 2,317 exports under twenty-four namespace
roots: 173 finite exports and the exact accepted 2,144-export Branch lineage.
`EXPORTED_FINITE_THEOREMS.txt` and `FINITE_CENSUS_PINS.json` freeze this descendant-inclusive
inventory with SHA-256 `1d8110918a0f1941835508532155a84724aa93d8b63238da8cfab278617e4317`.
The complete census and its negative controls remain part of the executable acceptance gate.
Publication's compiler discovery found 2,745 exports under twenty-eight namespace roots:
428 publication exports and the exact accepted 2,317-export finite lineage. Its inventory
and census pin freeze SHA-256 `3c9edadabfd4127e2a5192828a496ae890b3dc72f3c600ab839a576fc545a27c`;
the complete actual-module census and negative controls passed in its acceptance gate.
Slice-specific census pins bind each complete count and SHA-256.
The C# gate checks frozen identity and inherited membership, not column-based source scanning.
The Lean gate enumerates every theorem descendant, including generated declarations, and
uses transitive `Lean.collectAxioms` both during enumeration and for every frozen result record.
Only `propext`, `Classical.choice` and `Quot.sound` are permitted. Every root receives indented
nested unexpected-export and forbidden-axiom controls. Missing/duplicate/changed inventories,
missing/duplicate/malformed result records, unknown theorems and omitted exports have additional
negative controls. A failed baseline or unrelated elaboration/resource failure cannot satisfy
those controls. All finite and publication controls passed and remain required by their gates.
