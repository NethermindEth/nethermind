# WorldJournalExtractor

This standalone package extracts and admits the standard-mainnet, pre-commit journal shape used by Nethermind's `WorldState`, storage providers, and normal `StackAccessTracker`. It emits a theorem-free Lean transition, then proves that transition equal to a separately written extensional specification for individual operations and arbitrary finite traces.

The modeled projection distinguishes a missing account from a physically present empty account. It contains account fields, current persistent and transient storage, transaction-start persistent originals, warm account and storage-cell sets, ordered logs, the destroy set, and the transaction-wide created-account set. A separate raw account journal includes cache-only entries so snapshot positions retain Nethermind's exact `Count - 1` convention without exposing cache representation in the semantic projection.

The operation alphabet is exactly:

- account read, create, update, and delete;
- persistent read and write;
- transient read and write;
- warm account and warm cell;
- append log and add destroy;
- take and restore a combined frame snapshot.

`createAccount` takes only the production balance and nonce inputs and constructs an account with the
canonical empty storage root and empty code hash. It is the admitted `WorldState`/`StateProvider`
account-journal operation and does not
stand for `StackAccessTracker.WasCreated`: CREATE-frame initialization is outside this package, so
`createdThisTx` is an initial transaction-wide input that every admitted operation leaves unchanged.

The generated IR is admitted only when all pinned production files parse as C# 14, their SHA-256 identities match, every selected method, constructor, property, and field is unique, every IR operation binds to one exact canonical member signature, and required semantic effects occur in the admitted order. Persistent writes are rooted at the concrete `PersistentStorageProvider.Set` override and validated through `WorldState.Set`, the base `Set`, and `PushUpdate`; the active-scope check, metric increment, contract registration, and advisory warm hints are explicitly projected away under the premise recorded in `SCOPE.md`. Validation independently recomputes source bytes, canonical member syntax, IR and Lean bytes, artifact hashes, and aggregate hashes; alternate self-consistent manifests are rejected. JSON and Lean artifacts are canonical BOM-free, LF-only UTF-8 with one final LF.

## Claim

Lean proves the theorem-free generated transition extensionally equal to the handwritten specification for every finite operation trace. Snapshot restore independently rewinds accounts, persistent values, transient values, warm-account and warm-cell sets, logs, and the destroy set. Account restore preserves and re-appends predecessor-free `JustCache` entries exactly as the admitted source does, so later raw snapshots include them while the account projection remains unchanged. Transaction-start originals and `createdThisTx` survive restore, log order is preserved, and set insertions are idempotent.

The connection from the pinned C# members to the generated operation IR is a fail-closed syntax/profile admission, not a theorem about the CLR or Roslyn. It and the explicit runtime premises in `SCOPE.md` remain in the trusted boundary. The manifest makes that boundary reproducible and mutation-sensitive rather than presenting it as a proved compiler refinement.

This is a journal-level refinement only. It is not a proof about trie nodes, state roots, database persistence, storage clearing, code insertion or deposit, account reaping, provider commit, the block-access-list world state, tracing-access generation, unsafe/runtime library correctness, nested VM execution, gas, or the entire EVM.

## Reproduction

Run `Verify.ps1`. It regenerates into an isolated temporary directory, independently validates and byte-compares all three generated artifacts with `Generated/`, runs the standalone extractor tests with warnings as errors, builds both Lean targets, rejects proof declarations, examples, and placeholders in the generated transition, and rejects placeholders or axioms package-wide. The package is intentionally not added to a parent solution or parent verifier.
