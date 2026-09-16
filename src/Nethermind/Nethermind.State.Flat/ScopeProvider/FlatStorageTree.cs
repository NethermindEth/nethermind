// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    private BackgroundStorageTrie? _background;
    private readonly ILogManager _logManager;
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
        _logManager = logManager;
    }

    public Hash256 RootHash => _tree.RootHash;

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

    /// <inheritdoc/>
    public IWorldStateScopeProvider.IStorageWriteBatch? StartBackgroundWriteBatch()
    {
        if (!_scope.BackgroundStorageTrieUpdates) return null;
        if (_background is null || _background.IsStopped)
        {
            StorageTree tree = new(_tree.TrieStore, _tree.RootHash, _logManager);
            _background = new BackgroundStorageTrie(tree, _scope.BackgroundConcurrency);
        }
        return _background;
    }

    internal void StopBackgroundWrites() => _background?.Dispose();

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
        StopBackgroundWrites();
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

        return _background is null
            ? CreateTrieWriteBatch(estimatedEntries, onRootUpdated)
            : new DeferredStorageWriteBatch(this, estimatedEntries, onRootUpdated);
    }

    private IWorldStateScopeProvider.IStorageWriteBatch CreateTrieWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        BackgroundStorageTrie? prepared = _background?.Complete() == true ? _background : null;
        if (prepared is not null) _tree = prepared.Tree;
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch = new(
            estimatedEntries, _tree, onRootUpdated, _address, commit: true);
        return new StorageTreeBulkWriteBatch(trieBatch, this, prepared, onRootUpdated);
    }

    // Batch creation runs serially; defer draining and applying the pending tail to the contract worker.
    private sealed class DeferredStorageWriteBatch(
        FlatStorageTree storageTree,
        int estimatedEntries,
        Action<Address, Hash256> onRootUpdated) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private IWorldStateScopeProvider.IStorageWriteBatch? _batch;
        private bool _initialized;

        private IWorldStateScopeProvider.IStorageWriteBatch? GetBatch()
        {
            if (!_initialized)
            {
                _initialized = true;
                _batch = storageTree.CreateTrieWriteBatch(estimatedEntries, onRootUpdated);
            }
            return _batch;
        }

        public void Set(in UInt256 index, in UInt256 value) => GetBatch()!.Set(in index, in value);

        public void Clear() => GetBatch()!.Clear();

        public void Dispose() => GetBatch()?.Dispose();
    }

    // Normal scope: maintain the storage trie (for the root) and mirror values into the flat overlay.
    private sealed class StorageTreeBulkWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch trieBatch,
        FlatStorageTree storageTree,
        BackgroundStorageTrie? prepared,
        Action<Address, Hash256> onRootUpdated) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private bool _needsPreparedCommit = prepared is not null;

        public void Set(in UInt256 index, in UInt256 value)
        {
            if (prepared?.Contains(index, value) != true)
            {
                trieBatch.Set(in index, value);
                _needsPreparedCommit = false;
            }
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            prepared = null;
            _needsPreparedCommit = false;
            trieBatch.Clear();
            storageTree.ClearStorage();
        }

        public void Dispose()
        {
            trieBatch.Dispose();
            if (_needsPreparedCommit)
            {
                storageTree._tree.Commit();
                onRootUpdated(storageTree._address, storageTree.RootHash);
            }
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
