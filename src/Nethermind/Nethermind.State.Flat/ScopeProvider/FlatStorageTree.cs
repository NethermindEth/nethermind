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

    // Committed writes waiting for the early apply thread, in commit order.
    private ConcurrentQueue<(UInt256 Slot, UInt256 Value)>? _earlyWrites;
    // Owned by whoever holds _earlyState.
    private StorageTree? _earlyTree;
    private Dictionary<UInt256, UInt256>? _earlyApplied;
    private int _earlyState;
    private int _earlyQueued;
    private readonly int _earlyGeneration = scope.EarlyApplyGeneration;

    private sealed class Trees(StorageTree tree, StorageTree warmup)
    {
        public readonly StorageTree Tree = tree;
        public readonly StorageTree Warmup = warmup;
        // The root before any write, so a tree built on it copies on write.
        public readonly TrieNode? PreBlockRoot = tree.RootRef;
    }

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);

    // Set by the builder thread; read by the flush only after the scope has joined the builder. Cleared once a write
    // batch or a clear takes the trie, so only the first flush after the drain treats it as prebuilt.
    private volatile bool _builtByBuilder;

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
        StorageRootBuilder? builder = _scope.StorageRootBuilder;
        if (builder is not null && builder.TryEnqueue(this, in index, in value)) return;

        WarmUpSlot(index);
        if (!_scope.AppliesStorageWritesEarly || Volatile.Read(ref _earlyState) == EarlyClaimed) return;

        ConcurrentQueue<(UInt256 Slot, UInt256 Value)> writes = Volatile.Read(ref _earlyWrites) ?? CreateEarlyWrites();
        writes.Enqueue((index, value));
        // A set flag means a pass that has yet to clear it will see this write.
        if (Volatile.Read(ref _earlyQueued) == 0 && Interlocked.Exchange(ref _earlyQueued, 1) == 0) _scope.EarlyApplier.Enqueue(this);
    }

    private ConcurrentQueue<(UInt256 Slot, UInt256 Value)> CreateEarlyWrites()
    {
        ConcurrentQueue<(UInt256 Slot, UInt256 Value)> created = new();
        return Interlocked.CompareExchange(ref _earlyWrites, created, null) ?? created;
    }

    [SkipLocalsInit]
    internal void ApplyEarlyWrites()
    {
        // Cleared before the queue is read, so a hint landing during the pass queues the tree again.
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
            // No thread-pool work: it would run at normal priority.
            tree.BulkSet(entries, PatriciaTree.Flags.DoNotParallelize);
            tree.UpdateRootHash(canBeParallel: false);
            _scope.CountEarlyApplied(entries.Count);
        }
        catch
        {
            // A partly applied tree must never be adopted.
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

    /// <summary>Called once a pass has taken its writes. For tests.</summary>
    internal Action? OnEarlyPassDrained;

    private StorageTree CreateEarlyTree()
    {
        // Never the current root: an abandoned pass may still run while the batch writes into the block tree.
        StorageTree tree = new(new StorageTrieStoreWarmerAdapter(_bundle, AddressHash), _logManager);
        tree.SetRootHash(_storageRoot, false);
        tree.RootRef = GetTrees().PreBlockRoot;
        return tree;
    }

    private Dictionary<UInt256, UInt256>? AdoptEarlyTree(StorageTree tree)
    {
        if (Volatile.Read(ref _earlyWrites) is null) return null;

        int previous = Interlocked.Exchange(ref _earlyState, EarlyClaimed);
        if (previous == EarlyApplying)
        {
            // Mid-pass: waiting could take a scheduler tick, so the batch writes every slot instead.
            _scope.CountEarlyAbandoned();
            return null;
        }

        if (previous != EarlyIdle || _earlyTree is not { } earlyTree || _earlyApplied is not { Count: > 0 } applied) return null;

        tree.RootRef = earlyTree.RootRef;
        return applied;
    }

    // Builder thread only, until the scope drains the builder.
    internal void ApplyCommitted(in UInt256 index, in UInt256 value)
    {
        _builtByBuilder = true;
        Db.Metrics.IncrementParallelStorageRootWrites();
        SetInTree(in index, in value);
    }

    private void SetInTree(in UInt256 index, in UInt256 value)
    {
        Span<byte> buffer = stackalloc byte[32];
        value.ToBigEndian(buffer);
        GetTrees().Tree.Set(in index, value.IsZero ? StorageTree.ZeroBytes : buffer.WithoutLeadingZeros());
    }

    /// <summary>Drops the builder's writes, so the flush rebuilds the trie from the parent root.</summary>
    /// <remarks>Only after the scope has joined the builder.</remarks>
    internal void DropBuilderWrites()
    {
        if (!_builtByBuilder) return;
        Volatile.Write(ref _trees, null);
        _builtByBuilder = false;
    }

    internal void HashDirtyPaths() => GetTrees().Tree.UpdateRootHash(canBeParallel: false);

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
        _builtByBuilder = false;
        _bundle.ClearStorage(_address, AddressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        // Trieless scopes too: IWorldState.GetStorageRoot still reads RootHash there.
        GetTrees().Tree.RootHash = Keccak.EmptyTreeHash;
    }

    // Matches PatriciaTree.Commit, which splits the commit, hashing included, across threads above 4 writes.
    private const int MinWritesToHashInParallel = 4;

    // No trees means nothing was written, so there is nothing to commit.
    public void CommitTree() => Volatile.Read(ref _trees)?.Tree.Commit();

    /// <summary>Whether the storage trie holds nodes written since its last commit.</summary>
    internal bool HasUncommittedNodes => Volatile.Read(ref _trees)?.Tree.RootRef is { IsDirty: true };

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        // A trie-less (history-backed) scope can't maintain the storage trie (its persistence reader throws on
        // trie-node access), so it writes only the flat overlay. Pick the strategy once here.
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);
        // A tree the builder never touched (e.g. a batch driven directly, not via committed writes) takes the normal path.
        if (_builtByBuilder && _scope.UsePrebuiltStorageTries)
        {
            // Writes after the drain never reach the builder, so a later batch in the block applies its slots itself.
            _builtByBuilder = false;
            return new PrebuiltStorageWriteBatch(this, onRootUpdated);
        }

        StorageTree tree = GetTrees().Tree;
        Dictionary<UInt256, UInt256>? earlyApplied = AdoptEarlyTree(tree);
        // Deferred, the batch only hashes the tree and the scope commit writes its nodes after the block is reported
        // valid. The hash then goes parallel from the size at which a commit would split the tree across threads.
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch = new(estimatedEntries, tree, onRootUpdated, _address,
            commit: !_config.DeferStorageTrieCommit, minWritesToHashInParallel: MinWritesToHashInParallel);
        return earlyApplied is null
            ? new StorageTreeBulkWriteBatch(trieBatch, this)
            : new EarlyAppliedStorageWriteBatch(trieBatch, this, earlyApplied);
    }

    // For a tree that already holds the early writes: unchanged slots only update the flat overlay.
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
            // Clearing resets the tree, early writes included.
            _earlyApplied = null;
            trieBatch.Clear();
            storageTree.ClearStorage();
        }

        public void Dispose()
        {
            int restored = 0;
            if (_earlyApplied is { Count: > 0 } leftovers)
            {
                // Early writes the block ended at their pre-block value, which the flush skips.
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

    // ParallelStorageRoot: the builder already applied every committed write into the trie, so only mirror the values
    // into the flat overlay and commit. A clear resets the trie, after which the remaining writes must go in again.
    // As with the other batches, DeferStorageTrieCommit leaves the nodes to the scope commit and only hashes here.
    private sealed class PrebuiltStorageWriteBatch(
        FlatStorageTree storageTree,
        Action<Address, Hash256> onRootUpdated) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private bool _cleared;
        private int _writes;

        public void Set(in UInt256 index, in UInt256 value)
        {
            _writes++;
            if (_cleared) storageTree.SetInTree(in index, in value);
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            storageTree.ClearStorage();
            _cleared = true;
        }

        public void Dispose()
        {
            StorageTree tree = storageTree.GetTrees().Tree;
            if (storageTree._config.DeferStorageTrieCommit) tree.UpdateRootHash(_writes > MinWritesToHashInParallel);
            else tree.Commit();
            onRootUpdated(storageTree._address, tree.RootHash);
        }
    }

    // Trie-less scope: only the flat overlay is written; there is no storage trie to maintain.
    private sealed class FlatOverlayStorageWriteBatch(FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, in UInt256 value) => storageTree.Set(index, value);

        public void Clear() => storageTree.ClearStorage();

        public void Dispose() { }
    }
}
