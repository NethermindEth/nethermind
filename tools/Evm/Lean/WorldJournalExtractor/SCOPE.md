# Admitted world-journal boundary

## Production closure

The extractor pins 19 current source files and 87 exact methods, constructors, properties, and fields. Its principal bindings are:

| Abstract surface | Production source |
| --- | --- |
| account option and raw journal | `WorldState.GetAccount`, `TryGetAccount`, `HasEmptyAccountLeaf`, `CreateAccount`, `DeleteAccount`; `StateProvider.GetThroughCache`, front/backing-cache helpers, `PushJustCache`, `PushNew`, `PushUpdate`, `PushDelete`, `Push`, `TakeSnapshot`, `Restore` |
| persistent current/original | `WorldState.Get`, `GetOriginal`, `Set`; concrete `PersistentStorageProvider.Set`, active-scope and one-shot account-hint helpers; `PartialStorageProviderBase.Get`, `Set`, `TryGetCachedValue`, `PushUpdate`, `TakeSnapshot`, `Restore`; `PersistentStorageProvider.GetCurrentValue`, storage lookup, both `LoadFromTree` layers, backing load, `CaptureOriginalValue`, `GetOriginal`; scope warm-hint and metrics leaves |
| transient current | `WorldState.GetTransientState`, `SetTransientState`; `PartialStorageProviderBase.Get`, `Set`, `PushUpdate`, `TakeSnapshot`, `Restore`; `TransientStorageProvider.GetCurrentValue` |
| warm accounts/cells | both `StackAccessTracker.WarmUp` overloads and normal-mode `Restore` |
| logs and destroy set | `VirtualMachine.AddLog`, `StackAccessTracker.ToBeDestroyed`, tracker snapshot/restore, `JournalCollection`, `JournalSet`, and their exact `Count`/`Position` properties |
| sentinels and fresh account | `Snapshot.EmptyPosition`, `Resettable.EmptyPosition`, `StorageTree.ZeroBytes`; `Account` constructors/properties and `Keccak` empty-input fields |
| created this transaction boundary | `StackAccessTracker.WasCreated`, `VmState.Initialize` ordering, and the non-journaled `CreateList` field are admitted only to establish the input/restore boundary; their mutation is not an operation in this package |
| combined world snapshot | `WorldState.TakeSnapshot`, `WorldState.Restore`, and `Snapshot` |
| public world boundary | the exact `IWorldState` persistent/transient/account/snapshot signatures used by the slice |
| standard mainnet reachability | `BlockProcessingModule` bindings for `IWorldState` and `IVirtualMachine` |

The abstract combined snapshot composes the three `WorldState` positions with the four normal access-tracker positions. This is a semantic product; production obtains those positions in the two owning objects.

## Fixed boundary

- standard-mainnet `WorldState` only;
- one active execution journal with LIFO nested snapshot positions;
- before provider commit, trie update, and state-root calculation;
- normal execution with `isTracingAccess=false`;
- immutable storage and log-payload byte arrays after admission;
- address, word, hash, and nonce inputs already normalized to their production widths.

Reads expose extensional values. The raw account journal records a backing hit as `JustCache`; raw snapshot positions therefore advance. Restore removes semantic suffixes but retains and re-appends predecessor-free cache entries in production order, so a subsequent snapshot uses the re-appended raw count. The account projection ignores those entries and proves the read/restore sequence is a semantic stutter. Persistent originals are transaction-scoped and are not reverted with a frame. `CreateList` is also transaction-scoped and is not part of tracker snapshots. Persistent and transient changes have different positions and restore independently.

The abstract `createAccount` takes `(address, balance, nonce)`, constructs exactly the production
fresh account with the canonical empty storage root and empty code hash, journals that value, and leaves
`createdThisTx` unchanged. `StackAccessTracker.WasCreated` and CREATE-frame initialization supply that
set before the admitted frame boundary and are not modeled transitions here.

Production snapshot position `-1` is represented as Lean `0`; every other production position is shifted by one. Thus Lean positions are retained journal lengths, an order-preserving successor encoding of the exact `Count - 1` convention. A standard EVM persistent write is preceded by its current/original read. The abstract `writePersistent` operation itself only journals the value, matching `Set`; it does not silently synthesize an original.

The persistent-write source path is exactly `WorldState.Set` → `PersistentStorageProvider.Set` → `PartialStorageProviderBase.Set` → `PushUpdate`. Before delegating, the concrete override requires an active scope, increments storage-write metrics, and registers the contract storage; afterward it issues a slot warm hint and consumes at most one account warm hint for that contract. Those scope, metrics, storage-object memoization, and advisory hint effects are not fields of `WorldProjection`; the package projects them to stutter under premise 7 while retaining and proving the journal update. This does not claim refinement of the hint consumer, metrics publication, trie prewarming, or missing-scope exception path.

The physically empty account vector uses the exact Keccak-256 empty-trie root and empty-code hash as natural-number constants; it is not represented by four numeric zeroes.

At the public `IWorldState` surface, physical presence is reconstructed from `TryGetAccount` plus `HasEmptyAccountLeaf`; the concrete `StateProvider.GetThroughCache` binding supplies the exact optional account used by the projection. This retains absent, physically empty, and storage-only accounts as different cases even though `WorldState.GetAccount` alone returns `Account.TotallyEmpty` for absence.

The C# library behavior below is deliberately a premise, not a generated theorem:

1. `Dictionary` and `HashSet` implement extensional lookup and uniqueness for immutable keys.
2. `List` and `CollectionsMarshal.SetCount` preserve the retained prefix and order when suffixes are restored.
3. Storage and log-payload byte arrays are not mutated after being admitted to the provider/tracker.
4. production fixed-width types are correctly represented by the natural-number abstraction.
5. initial account and persistent maps agree with backing tree/backend reads at trace entry.
6. the pinned Keccak empty-input computations equal the explicit Lean digest constants.
7. persistent writes run inside an active scope, and their metrics, per-contract registration/memoization, and advisory slot/account warm hints do not mutate any admitted projection field.

The source-to-IR bridge is also trusted: Roslyn checks that selected syntax, exact owner/overload signatures, source identities, the admitted leaf closure, and required effect order match the closed profile, but Lean does not prove that profile to be the operational semantics of C# or the CLR. The closure ends at the explicitly listed collection, immutability, fixed-width, backing-input, and Keccak premises rather than claiming those library/runtime behaviors were extracted. Manifest validation recomputes these facts from source and exact artifact bytes rather than trusting manifest-provided digests. The formal theorem starts at the emitted transition.

Logger and transaction-tracer callbacks are not fields of the world projection; the package claims only the journal effect of their containing operations.

## Exclusions

`ClearStorage`, code insertion/deposit, `MarkStorageDestroyed`, empty-account reaping, commit/tree/root logic, `TracedAccessWorldState`, `BlockAccessListBasedWorldState`, BAL snapshots, `WasCreated` mutation, CREATE child initialization, and VM call/create control flow are outside this package. There is no state-root or database-equivalence claim.

## Proof inventory

The package proves helper transition agreement, per-operation transition agreement, arbitrary finite-trace agreement, the named non-nested and standard-trace theorems, generic and raw-account snapshot algebra, projection restoration, transaction-wide original/create-set preservation, canonical fresh-account construction, `createAccount` stutter on that set, separate persistent/transient restore, explicit preservation of every non-persistent projection field by a persistent write, semantic cache-read stutter with raw cache re-append positions, set idempotence, ordered log append, and restore failure without a captured snapshot. The standard-trace predicate requires a persistent original to have been captured before each persistent write and requires a live snapshot before restore.

Normative vectors exercise all 14 operations, missing versus physical-empty accounts, canonical fresh-account hashes, backing-read `JustCache` append/restore/re-snapshot, first-original capture, duplicate warm/destroy operations, ordered logs, two nested snapshot positions, and independent restoration of all seven journaled surfaces. Explicit mutants reverse each fragile semantic, including nonempty fresh-account hashes and dropped cache re-append behavior.
