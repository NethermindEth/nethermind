// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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
    private readonly StorageTree _tree;
    private readonly StorageTree _warmupStorageTree;
    private readonly Address _address;
    private readonly IFlatDbConfig _config;
    private readonly ITrieWarmer _trieCacheWarmer;
    private readonly FlatWorldStateScope _scope;
    private readonly SnapshotBundle _bundle;
    private readonly Hash256 _addressHash;
    private readonly Hash256 _parentRootHash;

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
        _parentRootHash = storageRoot;
        _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);

        StorageTrieStoreAdapter storageTrieAdapter = new(bundle, concurrencyQuota, _addressHash);
        StorageTrieStoreWarmerAdapter warmerStorageTrieAdapter = new(bundle, _addressHash);

        _tree = new StorageTree(storageTrieAdapter, storageRoot, logManager)
        {
            RootHash = storageRoot
        };

        // Set the rootref manually. Cut the call to find nodes by about 1/4th.
        _warmupStorageTree = new StorageTree(warmerStorageTrieAdapter, logManager);
        _warmupStorageTree.SetRootHash(storageRoot, false);
        _warmupStorageTree.RootRef = _tree.RootRef;

        _config = config;
    }

    public Hash256 RootHash => _tree.RootHash;

    internal Hash256 ParentRootHash => _parentRootHash;

    internal bool IsDisposed => _scope.IsDisposed;

    public byte[] Get(in UInt256 index)
    {
        byte[]? value = _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx);
        if (value is null || value.Length == 0)
        {
            value = StorageTree.ZeroBytes;
        }

        if (_config.VerifyWithTrie)
        {
            byte[] treeValue = _tree.Get(index);
            if (!Bytes.AreEqual(treeValue, value))
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {_tree.RootHash}, {index}. Tree: {treeValue?.ToHexString()} vs Flat: {value?.ToHexString()}. Self destruct it {_selfDestructKnownStateIdx}");
            }
        }

        return value!;
    }

    // Reads do not warm the trie: most reads come through the prewarmer, and read-only slots
    // (~30-40% of accesses per @weiihann's analysis) never need their trie path warmed because
    // they don't trigger commit-time tree updates. Warm-up is driven from HintSet on the write
    // path instead.
    public void HintSet(in UInt256 index, byte[]? value) => WarmUpSlot(index);

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

    private void Set(UInt256 slot, byte[] value) => _bundle.SetChangedSlot(_address, slot, value);

    public void SelfDestruct()
    {
        _bundle.Clear(_address, _addressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        _tree.RootHash = Keccak.EmptyTreeHash;
    }

    public void CommitTree() => _tree.Commit();

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        if (_scope.CollectSparseStorageBatches)
            return new SparseStorageBatch(this, estimatedEntries, onRootUpdated);

        TrieStoreScopeProvider.StorageTreeBulkWriteBatch storageTreeBulkWriteBatch = new(
                estimatedEntries,
                _tree,
                onRootUpdated,
                _address,
                commit: true);

        return new StorageTreeBulkWriteBatch(
            storageTreeBulkWriteBatch,
            this
        );
    }

    private sealed class StorageTreeBulkWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch storageTreeBulkWriteBatch,
        FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            storageTreeBulkWriteBatch.Set(in index, value);
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            storageTreeBulkWriteBatch.Clear();
            storageTree.SelfDestruct();
        }

        public void Dispose() => storageTreeBulkWriteBatch.Dispose();
    }

    internal sealed class SparseStorageBatch(
        FlatStorageTree storageTree,
        int estimatedEntries,
        Action<Address, Hash256> onRootUpdated) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private readonly FlatStorageTree _storageTree = storageTree;
        private readonly Action<Address, Hash256> _onRootUpdated = onRootUpdated;
        private readonly Dictionary<UInt256, SlotValue> _slots = new(estimatedEntries);
        private SparseTrieFinalStorageBatch? _finalState;
        private bool _clear;
        private bool _disposed;

        internal SparseTrieFinalStorageBatch FinalState =>
            _finalState ?? throw new InvalidOperationException("Sparse storage batch is not finalized.");

        public void Set(in UInt256 index, byte[] value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _slots[index] = new SlotValue(value, Changed: true);
            _storageTree.Set(index, value);
        }

        public void ObserveFinalValue(in UInt256 index, byte[] value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _slots.TryAdd(index, new SlotValue(value, Changed: false));
        }

        public void Clear()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_slots.Count != 0)
                throw new InvalidOperationException("Must call clear first in a storage write batch");
            _clear = true;
            _storageTree.SelfDestruct();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            SparseTrieFinalSlot[] exactSlots = new SparseTrieFinalSlot[_slots.Count];
            int index = 0;
            foreach (KeyValuePair<UInt256, SlotValue> entry in _slots)
                exactSlots[index++] = new SparseTrieFinalSlot(entry.Key, entry.Value.Value);

            _finalState = new SparseTrieFinalStorageBatch(
                _storageTree._address,
                _storageTree._parentRootHash,
                _clear,
                exactSlots);
            _storageTree._scope.RegisterSparseStorageBatch(this);
        }

        internal void ApplySparseRoot(Hash256 root)
        {
            _storageTree._tree.SetRootHash(root, resetObjects: true);
            _storageTree._warmupStorageTree.SetRootHash(root, resetObjects: true);
            _onRootUpdated(_storageTree._address, root);
        }

        internal void ReplayFallback()
        {
            _storageTree._tree.SetRootHash(_storageTree._parentRootHash, resetObjects: true);
            _storageTree._warmupStorageTree.SetRootHash(_storageTree._parentRootHash, resetObjects: true);

            int changedCount = 0;
            foreach (SlotValue value in _slots.Values)
            {
                if (value.Changed)
                    changedCount++;
            }

            using TrieStoreScopeProvider.StorageTreeBulkWriteBatch patriciaBatch = new(
                changedCount,
                _storageTree._tree,
                _onRootUpdated,
                _storageTree._address,
                commit: true);

            if (_clear)
                patriciaBatch.Clear();

            foreach (KeyValuePair<UInt256, SlotValue> entry in _slots)
            {
                if (entry.Value.Changed)
                    patriciaBatch.Set(entry.Key, entry.Value.Value);
            }

        }

        private readonly record struct SlotValue(byte[] Value, bool Changed);
    }
}
