// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
{
    private StorageTree _tree;
    private readonly StorageTree _warmupStorageTree;
    private readonly StorageTrieStoreAdapter _storageTrieAdapter;
    private readonly Hash256 _parentStorageRoot;
    private readonly Address _address;
    private readonly IFlatDbConfig _config;
    private readonly ITrieWarmer _trieCacheWarmer;
    private readonly FlatWorldStateScope _scope;
    private readonly SnapshotBundle _bundle;
    private readonly Hash256 _addressHash;
    private readonly ILogManager _logManager;

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx;

    // Background building (ParallelStorageRoot): committed writes coalesce here (last value per slot) until a job
    // applies them into _tree. _pendingLock guards the map and _job; at most one job runs per contract.
    private readonly Lock _pendingLock = new();
    private Dictionary<UInt256, UInt256>? _pendingWrites;
    private Task? _job;
    private volatile bool _builtByBuilder;
    private volatile bool _finalized;

    public FlatStorageTree(
        FlatWorldStateScope scope,
        ITrieWarmer trieCacheWarmer,
        SnapshotBundle bundle,
        IFlatDbConfig config,
        ConcurrencyController concurrencyQuota,
        Hash256 storageRoot,
        Address address,
        ILogManager logManager)
    {
        _scope = scope;
        _trieCacheWarmer = trieCacheWarmer;
        _bundle = bundle;
        _address = address;
        _addressHash = address.ToAccountPath.ToHash256();
        _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);
        _logManager = logManager;
        _parentStorageRoot = storageRoot;

        _storageTrieAdapter = new StorageTrieStoreAdapter(bundle, concurrencyQuota, _addressHash);
        StorageTrieStoreWarmerAdapter warmerStorageTrieAdapter = new(bundle, _addressHash);

        _tree = new StorageTree(_storageTrieAdapter, storageRoot, logManager);

        // Set the rootref manually. Cut the call to find nodes by about 1/4th.
        _warmupStorageTree = new StorageTree(warmerStorageTrieAdapter, logManager);
        _warmupStorageTree.SetRootHash(storageRoot, false);
        _warmupStorageTree.RootRef = _tree.RootRef;

        _config = config;
    }

    // Until the flush finalizes this trie its root is the parent's, exactly as on the serial path where the trie is
    // untouched during execution; a background job may have advanced _tree meanwhile.
    public Hash256 RootHash => _finalized ? _tree.RootHash : _parentStorageRoot;

    internal bool IsDisposed => _scope.IsDisposed;

    public void Get(in UInt256 index, out UInt256 value)
    {
        _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx, out UInt256? slotValue);
        value = slotValue.GetValueOrDefault();

        // A trie-less (history-backed) scope has no storage trie to verify against — the reader throws on trie-node
        // access, and a historical value verified against the current trie would be wrong anyway.
        if (_config.VerifyWithTrie && !_scope.Trieless)
        {
            _tree.Get(in index, out UInt256 treeValue);
            if (treeValue != value)
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {_tree.RootHash}, {index}. Tree: {treeValue} vs Flat: {value}. Self destruct it {_selfDestructKnownStateIdx}");
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
        if (builder is null)
        {
            WarmUpSlot(index);
            return;
        }

        lock (_pendingLock)
        {
            (_pendingWrites ??= [])[index] = value;
            if (_job is null && _pendingWrites.Count >= builder.BatchSize && builder.TryAcquireJobSlot())
                _job = Task.Run(() => RunBuilderJob(builder));
        }
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
        _tree.BulkSet(asRef, PatriciaTree.Flags.DoNotParallelize);
        Db.Metrics.AddParallelStorageRootWrites(count);
        Db.Metrics.AddParallelStorageRootApplyMicros((long)Stopwatch.GetElapsedTime(start).TotalMicroseconds);
    }

    private void HashDirtyPaths()
    {
        long start = Stopwatch.GetTimestamp();
        _tree.UpdateRootHash(canBeParallel: false);
        Db.Metrics.AddParallelStorageRootHashMicros((long)Stopwatch.GetElapsedTime(start).TotalMicroseconds);
    }

    /// <summary>
    /// Finalizes background building for this contract on the calling (flush) thread: waits for the in-flight job,
    /// applies the remaining tail, and reports whether the trie already holds every committed write.
    /// </summary>
    /// <remarks>
    /// Must run after the scope closed the builder, so no job can start concurrently. Returns false when no job ever
    /// touched the trie (the flush applies everything itself) or the builder faulted (the trie is reset to the
    /// parent root first, as a job may have left it half-applied).
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

    private void ResetTreeToParent()
    {
        _tree = new StorageTree(_storageTrieAdapter, _parentStorageRoot, _logManager);
        _warmupStorageTree.RootRef = _tree.RootRef;
        _builtByBuilder = false;
    }

    private void WarmUpSlot(UInt256 index)
    {
        if (_bundle.ShouldQueuePrewarm(_address, index))
        {
            // ShouldQueuePrewarm already marked the slot in the dedupe bloom, so a rejected push loses the hint for good.
            if (_trieCacheWarmer.PushSlotJob(this, index, _scope.HintSequenceId)
                || _trieCacheWarmer.PushSlotJobMpmc(this, index, _scope.HintSequenceId))
                _scope.IncrementOutstandingWarmups();
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

                _warmupStorageTree.WarmUpPath(key.BytesAsSpan);
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
        _bundle.ClearStorage(_address, _addressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        _tree.RootHash = Keccak.EmptyTreeHash;
    }

    public void CommitTree() => _tree.Commit();

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        // A trie-less (history-backed) scope can't maintain the storage trie (its persistence reader throws on
        // trie-node access), so it writes only the flat overlay. Pick the strategy once here.
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);
        if (_scope.StorageRootBuilderForFinalization is not null && !_finalized)
            return new DeferredStorageWriteBatch(this, estimatedEntries, onRootUpdated);
        return CreateFinalizedWriteBatch(estimatedEntries, onRootUpdated);
    }

    private IWorldStateScopeProvider.IStorageWriteBatch CreateFinalizedWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        bool prebuilt = FinishBuilding();
        _finalized = true;
        if (prebuilt)
        {
            Db.Metrics.IncrementParallelStorageRootPrebuiltTrees();
            return new PrebuiltStorageWriteBatch(this, onRootUpdated);
        }

        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch = new(estimatedEntries, _tree, onRootUpdated, _address, commit: true);
        return new StorageTreeBulkWriteBatch(trieBatch, this);
    }

    // Batches are constructed before the parallel flush starts. Resolve ownership on first use so an unfinished
    // builder cannot hold up unrelated contracts before their flush workers have been scheduled.
    private sealed class DeferredStorageWriteBatch(
        FlatStorageTree storageTree,
        int estimatedEntries,
        Action<Address, Hash256> onRootUpdated) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private IWorldStateScopeProvider.IStorageWriteBatch? _batch;
        private IWorldStateScopeProvider.IStorageWriteBatch Batch =>
            _batch ??= storageTree.CreateFinalizedWriteBatch(estimatedEntries, onRootUpdated);

        public void Set(in UInt256 index, in UInt256 value) => Batch.Set(in index, in value);

        public void Clear() => Batch.Clear();

        public void Dispose() => Batch.Dispose();
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
    private sealed class PrebuiltStorageWriteBatch(
        FlatStorageTree storageTree,
        Action<Address, Hash256> onRootUpdated) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private bool _cleared;

        public void Set(in UInt256 index, in UInt256 value)
        {
            if (_cleared)
            {
                Span<byte> buffer = stackalloc byte[32];
                value.ToBigEndian(buffer);
                storageTree._tree.Set(in index, value.IsZero ? StorageTree.ZeroBytes : buffer.WithoutLeadingZeros());
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
            storageTree._tree.Commit();
            onRootUpdated(storageTree._address, storageTree._tree.RootHash);
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
