# Shared PBT store cache implementation plan

## Objective

Add a process-wide, hash-aware cache for PBT leaf blobs and trie-node groups. The cache must remain correct across concurrent forks and reorgs, preserve `RefCountingMemory` ownership, and bound each PBT partition/value-kind independently.

## Configuration

Add six `ulong` byte-budget properties to `IPbtConfig` and `PbtConfig`. A zero budget disables that bucket.

| Bucket | Default |
|---|---:|
| Account leaf blobs | 64 MiB |
| Code leaf blobs | 32 MiB |
| Storage leaf blobs | 256 MiB |
| Account trie nodes | 128 MiB |
| Code trie nodes | 32 MiB |
| Storage trie nodes | 224 MiB |
| **Total** | **736 MiB** |

Document units, defaults, and zero-disable behavior in the public config API.

## Shared cache

1. Add one singleton PBT store cache, registered by both `PbtModule` and `PbtMirrorModule`.
2. Route entries by value kind (leaf/trie) and `PbtPartitions.Of(key)`, yielding six independently budgeted buckets.
3. Identify entries by logical key plus the value's expected hash. A logical-key match with a different hash is a miss.
4. Retain only non-null values. Tombstones remain authoritative in snapshot tiers but are not retained globally.
5. Give every retained value a cache-owned `RefCountingMemory` lease. A successful read acquires a separate caller lease. Replacement, eviction, clear, and disposal release only the cache lease.
6. Make all bucket operations structurally safe for concurrent folds and scopes.
7. Account retained bytes per bucket and evict only within the bucket whose budget was exceeded.

## Hash-aware PBT store API

Change all four `IPbtStore` operations to carry the hash identifying the value:

- `GetTrieNode`
- `SetTrieNode`
- `GetLeafBlob`
- `SetLeafBlob`

For trie groups, use the represented group's root node hash. For leaf blobs, use the stem leaf-subtree root. Persistence and snapshot keys remain unchanged; the hash exists for cache validation and population.

Propagate hashes already available in `TrieUpdater`:

- partition-root reads use the current partition root hash;
- internal-child reads use the parent boundary occupant hash;
- chain-target reads use `chain.TargetHash`;
- trie writes use the resulting `NodeResult.NodeHash()`;
- existing leaf reads use the pushed stem's stored subtree hash;
- leaf writes use `StemLeafBlob.RebuildState.SubtreeRoot`;
- removals use the known prior/result hash as appropriate.

Do not recompute hashes merely for cache access.

Update every `IPbtStore` implementation and test harness.

## Snapshot-bundle integration

Pass the singleton cache through every read-only and writable bundle construction path, including manager, overridable scopes, mirror/main processing, tests, benchmarks, and pre-genesis.

Hash-aware store read order:

1. writable bundle write buffer;
2. writable bundle local sealed layers, newest first;
3. shared cache, requiring the expected hash;
4. shared read-only snapshot layers, newest first;
5. persistence.

A tombstone found in a higher tier terminates the walk and must never fall through to the shared cache.

Populate the shared cache after a non-null hash-aware read falls through to snapshots or persistence. Hash-aware non-null writes also populate it under an independent cache lease. Preserve the existing per-block `PbtLeafBlobCache`; it separately shares flat reads with the fold and caches absences within one immutable branch view.

## Ownership and lifecycle

- DI owns and disposes the shared singleton.
- Bundles and `PbtDbManager` neither clear nor dispose it.
- Bundle sealing resets only the existing per-block leaf cache.
- Manual test/benchmark construction explicitly owns any manually created cache.
- Reads and writes racing eviction/disposal must never expose released bytes or double-release leases.

## Tests

Add parameterized coverage for all six routes and verify:

- key and hash must both match;
- same key/different hash misses;
- different key/same hash misses;
- replacement and eviction release old cache leases;
- disposal releases all retained leases;
- zero budget disables retention;
- exhausting one bucket cannot evict another;
- each bucket remains independently bounded.

Extend bundle/updater coverage for:

- cache hits bypass lower tiers;
- snapshot and persistence reads populate the cache;
- write buffers and tombstones shadow cached values;
- non-null writes populate without stealing the bundle/store lease;
- disposing one bundle does not clear the singleton cache;
- updater calls carry existing and resulting hashes correctly.

Add a reorg regression for both a leaf blob and trie group:

1. branch A stores value/hash A;
2. branch B stores value/hash B at the same logical key;
3. reading A rejects cached B;
4. A falls through to its own snapshot/persistence value;
5. A may repopulate the cache.

## Validation

1. Run `Nethermind.Pbt.Test` (the actual project path in this branch).
2. Run `Nethermind.State.Pbt.Test`.
3. Build `Nethermind.slnx` in release mode.
4. Inspect the final diff and working tree for unrelated changes.
