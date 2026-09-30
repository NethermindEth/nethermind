// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
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

    // Background building (ParallelStorageRoot): committed writes coalesce here (last value per slot) until a job
    // applies them into the tree. _pendingLock guards the map and _job; at most one job runs per contract.
    private readonly Lock _pendingLock = new();
    private Dictionary<UInt256, UInt256>? _pendingWrites;
    private Task? _job;
    private volatile bool _builtByBuilder;
    private volatile bool _finalized;

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

    // Until the flush finalizes this trie its root is the parent's, exactly as on the serial path where the trie is
    // untouched during execution; a background job may have advanced the tree meanwhile.
    public Hash256 RootHash => _finalized ? Volatile.Read(ref _trees)?.Tree.RootHash ?? _storageRoot : _storageRoot;

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
        if (builder is not null)
        {
            lock (_pendingLock)
            {
                (_pendingWrites ??= [])[index] = value;
                if (_job is null && _pendingWrites.Count >= builder.BatchSize && builder.TryAcquireJobSlot())
                    _job = Task.Run(() => RunBuilderJob(builder));
            }
            return;
        }

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

    private void RunBuilderJob(StorageRootBuilder builder)
    {
        bool slotHeld = true;
        bool appliedSinceHash = false;
        try
        {
            while (true)
            {
                ArrayPoolList<PatriciaTree.BulkSetEntry>? entries;
                lock (_pendingLock)
                {
                    if (_pendingWrites!.Count == 0)
                    {
                        // Still the owner here, so a hash pass is safe; re-check afterwards for writes that arrived meanwhile.
                        if (appliedSinceHash && builder.EagerHash && !builder.IsClosed)
                        {
                            appliedSinceHash = false;
                            entries = null;
                        }
                        else
                        {
                            builder.ReleaseJobSlot();
                            slotHeld = false;
                            _job = null;
                            return;
                        }
                    }
                    else
                    {
                        entries = TakePendingEntries();
                    }
                }

                if (entries is null)
                {
                    HashDirtyPaths();
                    continue;
                }

                using (entries)
                {
                    StorageRootBuilder.OnBeforeApplyForTests?.Invoke();
                    ApplyEntries(entries);
                    appliedSinceHash = true;
                }
            }
        }
        catch (Exception e)
        {
            builder.Fault(e);
            lock (_pendingLock) _job = null;
        }
        finally
        {
            if (slotHeld) builder.ReleaseJobSlot();
        }
    }

    // Caller holds _pendingLock and the map is non-empty.
    private ArrayPoolList<PatriciaTree.BulkSetEntry> TakePendingEntries()
    {
        ArrayPoolList<PatriciaTree.BulkSetEntry> entries = new(_pendingWrites!.Count);
        ValueHash256 key = ValueKeccak.Zero;
        Span<byte> buffer = stackalloc byte[32];
        foreach (KeyValuePair<UInt256, UInt256> kv in _pendingWrites)
        {
            StorageTree.ComputeKeyWithLookup(kv.Key, ref key);
            kv.Value.ToBigEndian(buffer);
            entries.Add(StorageTree.CreateBulkSetEntry(in key, buffer.WithoutLeadingZeros(), kv.Value.IsZero));
        }
        _pendingWrites.Clear();
        return entries;
    }

    private void ApplyEntries(ArrayPoolList<PatriciaTree.BulkSetEntry> entries)
    {
        long start = Stopwatch.GetTimestamp();
        int count = entries.Count;
        // ToRef hands the buffer over; the list is empty afterwards.
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> asRef = entries.ToRef();
        // Set before the apply: a fault half-way through must still reset the trie at the flush.
        _builtByBuilder = true;
        // Contracts share the job budget; nested trie parallelism would compete with execution.
        GetTrees().Tree.BulkSet(asRef, PatriciaTree.Flags.DoNotParallelize);
        Db.Metrics.AddParallelStorageRootWrites(count);
        Db.Metrics.AddParallelStorageRootApplyMicros((long)Stopwatch.GetElapsedTime(start).TotalMicroseconds);
    }

    private void HashDirtyPaths()
    {
        long start = Stopwatch.GetTimestamp();
        GetTrees().Tree.UpdateRootHash(canBeParallel: false);
        Db.Metrics.AddParallelStorageRootHashMicros((long)Stopwatch.GetElapsedTime(start).TotalMicroseconds);
    }

    /// <summary>
    /// Finalizes background building for this contract on the calling (flush) thread: waits for the in-flight job,
    /// applies the remaining tail, and reports whether the trie already holds every committed write.
    /// </summary>
    /// <remarks>
    /// Must run after the scope closed the builder, so no job can start concurrently. Returns false when no job ever
    /// touched the trie (the flush applies everything itself) or the builder faulted (the trie is reset to the
    /// parent root first, as a job may have left it half-applied). A trie an earlier flush of the block finalized
    /// misses every write since, which went past the closed builder, so a later batch applies its writes itself.
    /// </remarks>
    private bool FinishBuilding()
    {
        StorageRootBuilder? builder = _scope.StorageRootBuilderForFinalization;
        if (builder is null || _finalized) return false;

        long start = Stopwatch.GetTimestamp();
        WaitForJob();
        Db.Metrics.AddParallelStorageRootDrainWaitMicros((long)Stopwatch.GetElapsedTime(start).TotalMicroseconds);

        if (builder.IsFaulted)
        {
            if (_builtByBuilder) ResetTreeToParent();
            return false;
        }

        if (!_builtByBuilder) return false;

        ArrayPoolList<PatriciaTree.BulkSetEntry>? tail = null;
        lock (_pendingLock)
        {
            if (_pendingWrites is { Count: > 0 }) tail = TakePendingEntries();
        }
        if (tail is not null)
        {
            using (tail)
            {
                Db.Metrics.AddParallelStorageRootDrainBacklog(tail.Count);
                ApplyEntries(tail);
            }
        }
        return true;
    }

    internal void WaitForJob()
    {
        Task? job;
        lock (_pendingLock) job = _job;
        // Faults are recorded on the builder by the job itself; nothing propagates through the task.
        job?.Wait();
    }

    // The next use builds a fresh tree on the parent root.
    private void ResetTreeToParent()
    {
        Volatile.Write(ref _trees, null);
        _builtByBuilder = false;
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
        WaitForJob();
        _finalized = true;
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
        bool prebuilt = FinishBuilding();
        _finalized = true;
        if (prebuilt)
        {
            Db.Metrics.IncrementParallelStorageRootPrebuiltTrees();
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

    // ParallelStorageRoot: the trie already holds every committed write (coalesced to its final value), so only mirror
    // the values into the flat overlay and commit. A clear resets the trie, after which the remaining writes must go in again.
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
            if (_cleared)
            {
                Span<byte> buffer = stackalloc byte[32];
                value.ToBigEndian(buffer);
                storageTree.GetTrees().Tree.Set(in index, value.IsZero ? StorageTree.ZeroBytes : buffer.WithoutLeadingZeros());
            }
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
