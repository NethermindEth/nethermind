# Ordered parallel block reference

Status: independently reviewed and accepted handwritten reference imported by
`Eip803x.lean`; not a production extraction or refinement.

`ParallelBlockReference.lean` isolates the canonical merge theorem needed by
Nethermind's block-access-list executor. A parallel artifact is accepted only
when it reproduces the real transaction transition at the state prefix created
by every earlier artifact and reports the same receipt. Induction then proves
that applying all accepted artifacts in transaction order produces exactly the
same final state and receipt list as sequential execution. A rejected parallel
attempt runs the sequential relation directly, preserving both success and
failure results.

The candidate contains five general theorems. Eleven concrete checks exercise an
overlapping-write prefix, ordered receipts, fallback after rejection, failure
preservation, and mutations for a stale prefix, wrong receipt, reordered
artifacts, and a missing artifact.

This is not a production refinement. In particular, it does not prove that the
BAL wire representation contains every read and write, that worker snapshots
equal canonical prefixes, that `IncrementalValidation` establishes
`Certifies`, that state/journal adapters implement `Artifact.applyDelta`, or
that scheduling, cancellation, exceptions, roots, and persistence preserve the
relation. Those obligations must be extracted from the pinned production
parallel executor before its manifest gate can close.
