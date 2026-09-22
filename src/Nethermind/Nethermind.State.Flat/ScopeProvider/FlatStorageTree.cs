// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

public sealed class FlatStorageTree : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer, ITrieWarmer.IStorageWriteApplier
{
    private readonly StorageTree _tree;
    private readonly StorageTree _warmupStorageTree;
    private readonly Address _address;
    private readonly IFlatDbConfig _config;
    private readonly ITrieWarmer _trieCacheWarmer;
    private readonly FlatWorldStateScope _scope;
    private readonly SnapshotBundle _bundle;
    private readonly Hash256 _addressHash;

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx;

    // Storage writes committed while the block executes, applied to _tree by trie warmer jobs so the storage root
    // computation at the end of the block starts from a trie that already holds them and is mostly hashed. The
    // final write batch applies whatever is still pending first, so every committed write reaches the trie in
    // commit order, and the batch then re-sets values the trie already holds, which leaves those nodes untouched.
    private readonly Lock _writesLock = new();
    private ArrayPoolList<(UInt256 Index, UInt256 Value)>? _pendingWrites;
    private ArrayPoolList<(UInt256 Index, UInt256 Value)>? _spareWrites;
    private bool _writesScheduled;
    private bool _writesFaulted;

    // The root _tree last had without any early-applied write. A faulted job rolls the trie back to it, which leaves
    // the final write batch exactly the work it would have had without the early writes.
    private Hash256 _committedRoot;

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

        StorageTrieStoreAdapter storageTrieAdapter = new(bundle, concurrencyQuota, _addressHash);
        StorageTrieStoreWarmerAdapter warmerStorageTrieAdapter = new(bundle, _addressHash);

        _tree = new StorageTree(storageTrieAdapter, storageRoot, logManager);

        // Set the rootref manually. Cut the call to find nodes by about 1/4th.
        _warmupStorageTree = new StorageTree(warmerStorageTrieAdapter, logManager);
        _warmupStorageTree.SetRootHash(storageRoot, false);
        _warmupStorageTree.RootRef = _tree.RootRef;

        _config = config;
        _committedRoot = storageRoot;
    }

    public Hash256 RootHash => _tree.RootHash;

    internal bool IsDisposed => _scope.IsDisposed;

    // Exposed for tests to fail a background job after it changed the trie.
    internal Action? OnStorageWritesApplied;

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
    // path instead, and a scope that streams storage writes resolves the path by applying the write.
    public void HintSet(in UInt256 index, in UInt256 value)
    {
        if (!_scope.StreamsStorageWrites)
        {
            WarmUpSlot(index);
            return;
        }

        bool schedule;
        lock (_writesLock)
        {
            (_pendingWrites ??= new ArrayPoolList<(UInt256 Index, UInt256 Value)>(4)).Add((index, value));
            schedule = !_writesScheduled && !_writesFaulted;
            _writesScheduled |= schedule;
        }

        if (schedule && !_scope.TryScheduleStorageWrites(this))
        {
            lock (_writesLock) _writesScheduled = false;
        }
    }

    // Called by trie warmer. Never throws: a failure marks the trie faulted, which the final write batch resolves by
    // rolling it back.
    public void ApplyStorageWrites(int sequenceId)
    {
        if (!_scope.TryEnterStorageWrites(sequenceId))
        {
            lock (_writesLock) _writesScheduled = false;
            return;
        }

        try
        {
            while (TryTakePendingWrites(sequenceId, out ArrayPoolList<(UInt256 Index, UInt256 Value)>? writes))
            {
                try
                {
                    ApplyToTrie(writes);
                    OnStorageWritesApplied?.Invoke();

                    // Hashing waits for a quiet moment: a node hashed now and changed by a later write is hashed twice.
                    if (!RecycleAndCheckPending(writes)) _tree.HashDirtyNodes(canBeParallel: false);
                }
                catch (Exception)
                {
                    lock (_writesLock)
                    {
                        _writesFaulted = true;
                        _writesScheduled = false;
                    }

                    return;
                }
            }
        }
        finally
        {
            _scope.ExitStorageWrites();
        }
    }

    private bool TryTakePendingWrites(int sequenceId, [NotNullWhen(true)] out ArrayPoolList<(UInt256 Index, UInt256 Value)>? writes)
    {
        lock (_writesLock)
        {
            writes = _pendingWrites;
            if (writes is null || writes.Count == 0 || _writesFaulted || !_scope.AreStorageWritesOpen(sequenceId))
            {
                writes = null;
                _writesScheduled = false;
                return false;
            }

            _pendingWrites = _spareWrites;
            _spareWrites = null;
            return true;
        }
    }

    private bool RecycleAndCheckPending(ArrayPoolList<(UInt256 Index, UInt256 Value)> writes)
    {
        writes.Clear();
        lock (_writesLock)
        {
            if (_spareWrites is null) _spareWrites = writes;
            else writes.Dispose();

            return _pendingWrites is { Count: > 0 };
        }
    }

    [SkipLocalsInit]
    private void ApplyToTrie(ArrayPoolList<(UInt256 Index, UInt256 Value)> writes)
    {
        foreach (ref readonly (UInt256 Index, UInt256 Value) write in writes.AsSpan())
        {
            Unsafe.SkipInit(out EvmWord word);
            bool isZero = write.Value.IsZero;
            _tree.Set(in write.Index, isZero ? StorageTree.ZeroBytes : write.Value.ToMinimalBigEndian(ref word), isZero);
        }
    }

    /// <summary>
    /// Brings the trie up to every committed write before the final write batch touches it, or rolls it back to the
    /// last committed root if a background job failed part-way.
    /// </summary>
    /// <remarks>Runs with the scope's storage writes closed, so no job touches the trie concurrently.</remarks>
    private void CatchUpStorageWrites()
    {
        ArrayPoolList<(UInt256 Index, UInt256 Value)>? pending;
        bool faulted;
        lock (_writesLock)
        {
            pending = _pendingWrites;
            _pendingWrites = null;
            faulted = _writesFaulted;
            _writesFaulted = false;
        }

        try
        {
            if (faulted) _tree.RootHash = _committedRoot;
            else if (pending is not null) ApplyToTrie(pending);
        }
        finally
        {
            pending?.Dispose();
        }
    }

    private void DiscardStorageWrites()
    {
        lock (_writesLock)
        {
            _pendingWrites?.Dispose();
            _pendingWrites = null;
            _writesFaulted = false;
        }
    }

    /// <summary>Returns the pooled write buffers once the scope is done with this tree.</summary>
    /// <remarks>Runs with the scope's storage writes closed, so no job holds a buffer.</remarks>
    internal void ReleaseStorageWrites()
    {
        lock (_writesLock)
        {
            _pendingWrites?.Dispose();
            _pendingWrites = null;
            _spareWrites?.Dispose();
            _spareWrites = null;
        }
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
        // The writes committed before the clear go with it.
        DiscardStorageWrites();
        _bundle.ClearStorage(_address, _addressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        _tree.RootHash = Keccak.EmptyTreeHash;
        _committedRoot = Keccak.EmptyTreeHash;
    }

    public void CommitTree() => _tree.Commit();

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        // A trie-less (history-backed) scope can't maintain the storage trie (its persistence reader throws on
        // trie-node access), so it writes only the flat overlay. Pick the strategy once here.
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);

        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch = new(estimatedEntries, _tree, onRootUpdated, _address, commit: true);
        return new StorageTreeBulkWriteBatch(trieBatch, this);
    }

    // Normal scope: maintain the storage trie (for the root) and mirror values into the flat overlay.
    private sealed class StorageTreeBulkWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch,
        FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private bool _caughtUp;

        public void Set(in UInt256 index, in UInt256 value)
        {
            CatchUp();
            trieBatch.Set(in index, value);
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            _caughtUp = true;
            trieBatch.Clear();
            storageTree.ClearStorage();
        }

        public void Dispose()
        {
            CatchUp();
            trieBatch.Dispose();
            storageTree._committedRoot = storageTree._tree.RootHash;
        }

        private void CatchUp()
        {
            if (_caughtUp) return;
            _caughtUp = true;
            storageTree.CatchUpStorageWrites();
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
