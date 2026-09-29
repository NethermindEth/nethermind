// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree(
    FlatWorldStateScope scope,
    ITrieWarmer trieCacheWarmer,
    SnapshotBundle bundle,
    IFlatDbConfig config,
    Hash256 storageRoot,
    Address address,
    ILogManager logManager) : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
{
    private readonly Address _address = address;
    private readonly IFlatDbConfig _config = config;
    private readonly ITrieWarmer _trieCacheWarmer = trieCacheWarmer;
    private readonly FlatWorldStateScope _scope = scope;
    private readonly SnapshotBundle _bundle = bundle;
    private readonly ILogManager _logManager = logManager;
    private readonly Hash256 _storageRoot = storageRoot;
    private Hash256? _addressHash;

    private Trees? _trees;

    private const int EarlyIdle = 0;
    private const int EarlyApplying = 1;
    private const int EarlyClaimed = 2;

    // Committed slot writes waiting for the early apply thread, in commit order. Created by the first hint.
    private ConcurrentQueue<(UInt256 Slot, UInt256 Value)>? _earlyWrites;
    // The early apply thread's own tree. It starts from the block's pre-block root and changes nodes copy-on-write,
    // so the block's tree only sees its writes once the block-end batch adopts its root.
    private StorageTree? _earlyTree;
    // The value each slot has in _earlyTree. Owned by whoever holds _earlyState.
    private Dictionary<UInt256, UInt256>? _earlyApplied;
    private int _earlyState;
    private int _earlyQueued;
    private readonly int _earlyGeneration = scope.EarlyApplyGeneration;

    private sealed class Trees(StorageTree tree, StorageTree warmup)
    {
        public readonly StorageTree Tree = tree;
        public readonly StorageTree Warmup = warmup;
        // Tree's root before its first write: sealed, or null for an empty trie, so a tree built on it copies on write.
        public readonly TrieNode? PreBlockRoot = tree.RootRef;
    }

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);

    private Hash256 AddressHash => _addressHash ??= _address.ToAccountPath.ToHash256();

    private Trees GetTrees() => Volatile.Read(ref _trees) ?? CreateTrees();

    private Trees CreateTrees()
    {
        Hash256 addressHash = AddressHash;
        StorageTree tree = new(new StorageTrieStoreAdapter(_bundle, addressHash), _storageRoot, _logManager);

        // Set the rootref manually. Cut the call to find nodes by about 1/4th.
        StorageTree warmup = new(new StorageTrieStoreWarmerAdapter(_bundle, addressHash), _logManager);
        warmup.SetRootHash(_storageRoot, false);
        warmup.RootRef = tree.RootRef;

        Trees created = new(tree, warmup);
        return Interlocked.CompareExchange(ref _trees, created, null) ?? created;
    }

    public Hash256 RootHash => Volatile.Read(ref _trees)?.Tree.RootHash ?? _storageRoot;

    internal bool IsDisposed => _scope.IsDisposed;

    public void Get(in UInt256 index, out UInt256 value)
    {
        _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx, out UInt256? slotValue);
        value = slotValue.GetValueOrDefault();

        // A trie-less (history-backed) scope has no storage trie to verify against — the reader throws on trie-node
        // access, and a historical value verified against the current trie would be wrong anyway.
        if (_config.VerifyWithTrie && !_scope.Trieless)
        {
            StorageTree tree = GetTrees().Tree;
            tree.Get(in index, out UInt256 treeValue);
            if (treeValue != value)
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {tree.RootHash}, {index}. Tree: {treeValue} vs Flat: {value}. Self destruct it {_selfDestructKnownStateIdx}");
            }
        }
    }

    // Reads do not warm the trie: most reads come through the prewarmer, and read-only slots
    // (~30-40% of accesses per @weiihann's analysis) never need their trie path warmed because
    // they don't trigger commit-time tree updates. Warm-up is driven from HintSet on the write
    // path instead.
    public void HintSet(in UInt256 index) => WarmUpSlot(index);

    public void HintSet(in UInt256 index, in UInt256 value)
    {
        WarmUpSlot(index);
        if (!_scope.AppliesStorageWritesEarly || Volatile.Read(ref _earlyState) == EarlyClaimed) return;

        ConcurrentQueue<(UInt256 Slot, UInt256 Value)> writes = Volatile.Read(ref _earlyWrites) ?? CreateEarlyWrites();
        writes.Enqueue((index, value));
        // The tree is usually still queued, and a plain read then skips the locked write. Enqueue reserves the write's
        // slot with an interlocked operation, which orders it before this read, and a pass clears the flag with one
        // before it reads the queue: if this still sees the flag set, the pass that clears it sees the write.
        if (Volatile.Read(ref _earlyQueued) == 0 && Interlocked.Exchange(ref _earlyQueued, 1) == 0) _scope.EarlyApplier.Enqueue(this);
    }

    private ConcurrentQueue<(UInt256 Slot, UInt256 Value)> CreateEarlyWrites()
    {
        ConcurrentQueue<(UInt256 Slot, UInt256 Value)> created = new();
        return Interlocked.CompareExchange(ref _earlyWrites, created, null) ?? created;
    }

    /// <summary>
    /// Applies the committed writes queued so far to the early tree and hashes it. Runs on the early apply thread.
    /// </summary>
    [SkipLocalsInit]
    internal void ApplyEarlyWrites()
    {
        // Cleared first, and before the queue is read, so a hint that lands during this pass queues the tree again.
        Interlocked.Exchange(ref _earlyQueued, 0);
        if (_scope.EarlyApplyClosed || _scope.EarlyApplyGeneration != _earlyGeneration
            || Interlocked.CompareExchange(ref _earlyState, EarlyApplying, EarlyIdle) != EarlyIdle) return;

        bool leased = false;
        try
        {
            ConcurrentQueue<(UInt256 Slot, UInt256 Value)>? writes = Volatile.Read(ref _earlyWrites);
            if (writes is null || writes.IsEmpty || !(leased = _bundle.TryLeaseReadOnlyBundle())) return;

            Dictionary<UInt256, UInt256> applied = _earlyApplied ??= [];
            Dictionary<UInt256, UInt256> latest = new(writes.Count);
            while (writes.TryDequeue(out (UInt256 Slot, UInt256 Value) write)) latest[write.Slot] = write.Value;

            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(latest.Count);
            Unsafe.SkipInit(out EvmWord word);
            ValueHash256 key = default;
            foreach ((UInt256 slot, UInt256 value) in latest)
            {
                if (applied.TryGetValue(slot, out UInt256 current) && current == value) continue;

                bool isZero = value.IsZero;
                StorageTree.ComputeKeyWithLookup(slot, ref key);
                entries.Add(StorageTree.CreateBulkSetEntry(key, isZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref word), isZero));
                applied[slot] = value;
            }

            if (entries.Count == 0) return;

            OnEarlyPassDrained?.Invoke();
            StorageTree tree = _earlyTree ??= CreateEarlyTree();
            // Nothing here may fan out onto the thread pool, where it would compete at normal priority.
            tree.BulkSet(entries, PatriciaTree.Flags.DoNotParallelize);
            // Hashing now leaves the block-end pass only the paths written after this one.
            tree.UpdateRootHash(canBeParallel: false);
            _scope.CountEarlyApplied(entries.Count);
        }
        catch
        {
            // A partly applied tree no longer matches _earlyApplied, so it must never be adopted.
            _earlyTree = null;
            _earlyApplied = null;
            Volatile.Write(ref _earlyState, EarlyClaimed);
            throw;
        }
        finally
        {
            if (leased) _bundle.ReleaseReadOnlyBundleLease();
            Interlocked.CompareExchange(ref _earlyState, EarlyIdle, EarlyApplying);
        }
    }

    /// <summary>Whether every queued write has been applied to the early tree. For tests.</summary>
    internal bool EarlyWritesDrained => Volatile.Read(ref _earlyWrites) is not { IsEmpty: false } && Volatile.Read(ref _earlyState) != EarlyApplying && Volatile.Read(ref _earlyQueued) == 0;

    /// <summary>Runs on the early apply thread once a pass has taken its writes, before it builds on the tree. For tests.</summary>
    internal Action? OnEarlyPassDrained;

    private StorageTree CreateEarlyTree()
    {
        // Reads go through the warmer's adapter, which is safe off the block thread. The tree starts from the block
        // tree's root before its first write, never its current one: a pass the block-end batch has abandoned can
        // still be running while the batch writes into the block tree, and building on the batch's unsealed nodes
        // would change them in place. Sharing the untouched root, like the warm-up tree, keeps the nodes both resolve
        // in one place.
        StorageTree tree = new(new StorageTrieStoreWarmerAdapter(_bundle, AddressHash), _logManager);
        tree.SetRootHash(_storageRoot, false);
        tree.RootRef = GetTrees().PreBlockRoot;
        return tree;
    }

    /// <summary>
    /// Stops the early apply thread for this tree and, unless it was mid-pass, moves its writes into
    /// <paramref name="tree"/>. Returns the value of every slot it applied, or null when nothing was adopted.
    /// </summary>
    private Dictionary<UInt256, UInt256>? AdoptEarlyTree(StorageTree tree)
    {
        if (Volatile.Read(ref _earlyWrites) is null) return null;

        int previous = Interlocked.Exchange(ref _earlyState, EarlyClaimed);
        if (previous == EarlyApplying)
        {
            // The thread was preempted mid-pass. Waiting could take a scheduler tick, so its work is dropped and the
            // batch writes every slot, as it would without the thread.
            _scope.CountEarlyAbandoned();
            return null;
        }

        if (previous != EarlyIdle || _earlyTree is not { } earlyTree || _earlyApplied is not { Count: > 0 } applied) return null;

        tree.RootRef = earlyTree.RootRef;
        return applied;
    }

    private void WarmUpSlot(UInt256 index)
    {
        if (_bundle.ShouldQueuePrewarm(_address, index))
        {
            // ShouldQueuePrewarm already marked the slot in the dedupe bloom, so a rejected push loses the hint for good.
            _scope.IncrementOutstandingWarmups();
            if (!_trieCacheWarmer.PushSlotJob(this, index, _scope.HintSequenceId)
                && !_trieCacheWarmer.PushSlotJobMpmc(this, index, _scope.HintSequenceId))
                _scope.DecrementOutstandingWarmups();
        }
    }

    // Called by trie warmer.
    public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
    {
        try
        {
            if (_scope.HintSequenceId != sequenceId || _scope._pausePrewarmer)
            {
                return false;
            }

            if (!_bundle.TryLeaseReadOnlyBundle())
            {
                return false;
            }

            try
            {
                // Note: storage tree root not changed after write batch. Also not cleared. So the result is not correct.
                // this is just to warm up the nodes.
                ValueHash256 key = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(index, ref key);

                GetTrees().Warmup.WarmUpPath(key.BytesAsSpan);
                return true;
            }
            finally
            {
                _bundle.ReleaseReadOnlyBundleLease();
            }
        }
        finally
        {
            _scope.DecrementOutstandingWarmups();
        }
    }

    private void Set(in UInt256 slot, in UInt256 value) => _bundle.SetChangedSlot(_address, slot, value);

    internal void ClearStorage()
    {
        _bundle.ClearStorage(_address, AddressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        // Trieless scopes too: IWorldState.GetStorageRoot still reads RootHash there.
        GetTrees().Tree.RootHash = Keccak.EmptyTreeHash;
    }

    // No trees means nothing was written, so there is nothing to commit.
    public void CommitTree() => Volatile.Read(ref _trees)?.Tree.Commit();

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        // A trie-less (history-backed) scope can't maintain the storage trie (its persistence reader throws on
        // trie-node access), so it writes only the flat overlay. Pick the strategy once here.
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);

        StorageTree tree = GetTrees().Tree;
        Dictionary<UInt256, UInt256>? earlyApplied = AdoptEarlyTree(tree);
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch = new(estimatedEntries, tree, onRootUpdated, _address, commit: true);
        return earlyApplied is null
            ? new StorageTreeBulkWriteBatch(trieBatch, this)
            : new EarlyAppliedStorageWriteBatch(trieBatch, this, earlyApplied);
    }

    // Normal scope whose tree already holds the early apply thread's writes: a slot written with the value it already
    // has only goes to the flat overlay, and early writes the block did not end with are put back.
    private sealed class EarlyAppliedStorageWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch,
        FlatStorageTree storageTree,
        Dictionary<UInt256, UInt256> earlyApplied) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private Dictionary<UInt256, UInt256>? _earlyApplied = earlyApplied;
        private int _reused;

        public void Set(in UInt256 index, in UInt256 value)
        {
            storageTree.Set(index, value);
            if (_earlyApplied is not null && _earlyApplied.Remove(index, out UInt256 applied) && applied == value)
            {
                trieBatch.MarkSet();
                _reused++;
                return;
            }

            trieBatch.Set(in index, value);
        }

        public void Clear()
        {
            // Clearing resets the tree, early writes included, so every later slot goes through the batch.
            _earlyApplied = null;
            trieBatch.Clear();
            storageTree.ClearStorage();
        }

        public void Dispose()
        {
            int restored = 0;
            if (_earlyApplied is { Count: > 0 } leftovers)
            {
                // Slots written early that ended the block at the value they started it with, which the flush skips.
                // The flat overlay holds nothing new for them, so it still returns that value.
                foreach (UInt256 slot in leftovers.Keys)
                {
                    storageTree.Get(slot, out UInt256 value);
                    trieBatch.Set(slot, value);
                    restored++;
                }
            }

            storageTree._scope.CountEarlyReconciled(_reused, restored);
            trieBatch.Dispose();
        }
    }

    // Normal scope: maintain the storage trie (for the root) and mirror values into the flat overlay.
    private sealed class StorageTreeBulkWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch,
        FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, in UInt256 value)
        {
            trieBatch.Set(in index, value);
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            trieBatch.Clear();
            storageTree.ClearStorage();
        }

        public void Dispose() => trieBatch.Dispose();
    }

    // Trie-less scope: only the flat overlay is written; there is no storage trie to maintain.
    private sealed class FlatOverlayStorageWriteBatch(FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, in UInt256 value) => storageTree.Set(index, value);

        public void Clear() => storageTree.ClearStorage();

        public void Dispose() { }
    }
}
