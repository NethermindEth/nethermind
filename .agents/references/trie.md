# Trie Module Context

Knowledge specific to Nethermind.Trie, Nethermind.Trie.Pruning, and related trie code.

## TreePath management

- `GetChildWithChildPath` expects the caller's TreePath to already reflect the child's position.
  For **branch nodes**, it appends the nibble internally.
  For **extension nodes**, the caller must `AppendMut(node.Key)` BEFORE the call, then `TruncateMut` after.
  The asymmetry is the trap — 5 sites in PatriciaTree.cs follow this pattern.
- In 16-child branch loops: hoist `AppendMut(0)` before the loop and `TruncateOne()` after.
  Use `SetLast(i)` inside the loop body to avoid 15 unnecessary append/truncate pairs.

## Pruning

- `WaitForPruning()` then `SyncPruneQueue()` for deterministic test pruning.
  `Prune()` in `FinishBlockCommit` is a no-op when `_pruningTask.IsCompleted` is false.
  `WaitForPruning()` waits for the running task but does NOT drain the current commit set.
  `SyncPruneQueue()` (marked "Testing purpose only") handles that.
- All boundary checks using `_maxDepth` or `LastPersistedBlockNumber` must be gated on
  `_deleteOldNodes`. In archive mode (`_deleteOldNodes == false`), no nodes are deleted,
  so these boundaries are meaningless and would incorrectly reject valid old state roots.

## Hashing

- `KeccakCache.Compute` > `ValueKeccak.Compute` for inputs that repeat across blocks
  (addresses, storage keys, public keys). KeccakCache is a 16MB set-associative cache
  with seqlock reads. 23 hot-path call sites already use it.
- `ValueKeccak.Compute` > `Keccak.Compute` for one-off hashes (avoids heap-allocating Hash256).
