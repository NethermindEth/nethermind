# State Module Context

Knowledge specific to Nethermind.State, storage providers, and flat state.

## Pooled collections

- `StackList<T>` uses `StaticPool`-based pooling. When removing from intra-block caches
  (e.g., `Restore`/snapshot revert), call `Return()` explicitly. Missing it leaks pool
  instances and increases allocation pressure.
- `ArrayListCore.Truncate` and any pooled collection resize must call `ClearTail` for
  reference types (guarded by `RuntimeHelpers.IsReferenceOrContainsReferences<T>()`).
  Abandoned slots still hold references, preventing GC collection.

## Transaction lifecycle

- `Transaction` has three places that must enumerate ALL fields: `Return()` (reset pooled
  object), `CopyTo()` (copy for RLP decode), and constructors. When adding a new field,
  all three must be updated. The compiler provides no enforcement.

## Block access lists (EIP-7928)

- `BlockAccessList` uses journal-based rollback. On successful tx: `IncrementBlockAccessIndex`.
  On failed tx: `RollbackCurrentIndex` (combines `Restore(0)` + clear changes + `Index--`).
  Missing the rollback leaves phantom state changes and corrupts the index counter.

## BlockNumber sentinel values

- `BlockNumber` carries sentinels: `PreGenesis` = -1, `Sync` = `long.MinValue`
- Any method accepting `BlockNumber` for range queries (`SortedSet.GetViewBetween`),
  binary search, or `Span` slicing must validate before assuming non-negative
- `GetViewBetween(lower, upper)` throws `ArgumentException` when lower > upper,
  which happens silently with sentinel inputs

## World state scoping

- `CollectionsMarshal.GetValueRefOrAddDefault` for single-lookup dictionary get-or-create
  on hot paths. `DictionaryExtensions.GetOrAdd` and `Increment` wrap this API.
