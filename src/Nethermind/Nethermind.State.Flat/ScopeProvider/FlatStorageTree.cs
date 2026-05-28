// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

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

    /// <summary>Exposed for sparse trie proof prefetching (M5).</summary>
    public Hash256 AccountPathHash => _addressHash;

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
            if (_trieCacheWarmer.PushSlotJob(this, index, _scope.HintSequenceId))
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

            // Note: storage tree root not changed after write batch. Also not cleared. So the result is not correct.
            // this is just to warm up the nodes.
            ValueHash256 key = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(index, ref key);

            _warmupStorageTree.WarmUpPath(key.BytesAsSpan);
            return true;
        }
        finally
        {
            _scope.DecrementOutstandingWarmups();
        }
    }

    public byte[] Get(in ValueHash256 hash) => throw new NotSupportedException("Not supported");

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
        // M3: when sparse storage is authoritative, bypass Patricia storage tree work
        // entirely. The sparse computer computes per-contract storage roots; the resulting
        // root is fed back via onRootUpdated so the account RLP picks it up.
        if (_scope.UseSparseStorageRoot)
        {
            return new SparseStorageWriteBatch(this, _scope.SparseRootComputerInternal!, onRootUpdated, estimatedEntries);
        }

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

    private class StorageTreeBulkWriteBatch(
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

    /// <summary>
    /// Sparse-only storage write batch. Replaces <see cref="TrieStoreScopeProvider.StorageTreeBulkWriteBatch"/>
    /// when the sparse trie is authoritative for storage. Collects per-slot changes into a hashed
    /// dictionary, feeds them to <see cref="SparseRootComputer.AddStorageChanges"/>, and computes
    /// the new storage root on Dispose. The Patricia storage tree is not touched.
    /// </summary>
    private sealed class SparseStorageWriteBatch(
        FlatStorageTree storageTree,
        SparseRootComputer sparseRootComputer,
        Action<Address, Hash256> onRootUpdated,
        int estimatedEntries) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private readonly FlatStorageTree _storageTree = storageTree;
        private readonly SparseRootComputer _sparseRootComputer = sparseRootComputer;
        private readonly Action<Address, Hash256> _onRootUpdated = onRootUpdated;
        private readonly Hash256 _previousStorageRoot = storageTree._tree.RootHash;
        private readonly Dictionary<Hash256, LeafUpdate> _slotUpdates = new(estimatedEntries);
        private bool _hasSelfDestruct;
        private bool _wasSetCalled;

        public void Set(in UInt256 index, byte[] value)
        {
            _wasSetCalled = true;
            ValueHash256 slotKey = default;
            StorageTree.ComputeKeyWithLookup(index, ref slotKey);
            Hash256 slotHash = slotKey.ToCommitment();

            // Mirror Patricia's storage semantics: zero/empty value deletes the slot.
            // PatriciaTree's StorageTree.Set treats empty/zero-RLP as delete; we do the same.
            bool isDelete = value is null || value.Length == 0 || (value.Length == 1 && value[0] == 0x80);
            _slotUpdates[slotHash] = isDelete ? LeafUpdate.Deleted() : LeafUpdate.Changed(value!);

            // Keep the SnapshotBundle in sync for storage READS during this block.
            _storageTree.Set(index, value!);
        }

        public void Clear()
        {
            if (_wasSetCalled) throw new InvalidOperationException("Must call Clear before any Set in a storage write batch");
            _hasSelfDestruct = true;
            _storageTree.SelfDestruct();
        }

        public void Dispose()
        {
            if (!_wasSetCalled && !_hasSelfDestruct) return;

            // Patricia semantics: Clear may be followed by Sets in the same batch (self-destruct
            // then redeploy). When _hasSelfDestruct is set, wipe the sparse storage trie first
            // so subsequent Sets land in an empty trie rooted at EmptyTreeHash.
            Hash256 effectivePrevRoot = _previousStorageRoot;
            if (_hasSelfDestruct)
            {
                _sparseRootComputer.Trie.WipeStorage(_storageTree._addressHash);
                effectivePrevRoot = Keccak.EmptyTreeHash;
            }

            if (!_wasSetCalled)
            {
                _storageTree._tree.RootHash = Keccak.EmptyTreeHash;
                _onRootUpdated(_storageTree._address, Keccak.EmptyTreeHash);
                return;
            }

            _sparseRootComputer.AddStorageChanges(_storageTree._addressHash, effectivePrevRoot, _slotUpdates);
            Hash256 newRoot = _sparseRootComputer.ComputeStorageRoot(_storageTree._addressHash);

            // Keep FlatStorageTree.RootHash in sync so subsequent same-block reads via Get
            // (and the eventual account encoding) see the post-batch root.
            _storageTree._tree.RootHash = newRoot;
            _onRootUpdated(_storageTree._address, newRoot);
        }
    }
}
