// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatStorageTree : IWorldStateScopeProvider.IStorageTree
{
    private readonly StorageTree _tree;
    private readonly StorageTree _warmupStorageTree;
    private readonly Address _address;
    private readonly FlatDiffRepository.Configuration _config;
    private readonly ITrieWarmer _trieCacheWarmer;
    private readonly FlatWorldStateScope _scope;
    private readonly SnapshotBundle _bundle;
    private readonly Hash256 _addressHash;
    private readonly StorageTrieStoreAdapter _storageTrieAdapter;
    private readonly StorageTrieStoreAdapter _warmerStorageTrieAdapter;

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx;

    public FlatStorageTree(
        FlatWorldStateScope scope,
        ITrieWarmer trieCacheWarmer,
        SnapshotBundle bundle,
        FlatDiffRepository.Configuration config,
        ConcurrencyQuota concurrencyQuota,
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

        _storageTrieAdapter = new StorageTrieStoreAdapter(bundle, concurrencyQuota, _addressHash, _selfDestructKnownStateIdx,
            isTrieWarmer: false);
        _warmerStorageTrieAdapter = new StorageTrieStoreAdapter(bundle, concurrencyQuota, _addressHash, _selfDestructKnownStateIdx,
            isTrieWarmer: true);

        _tree = new StorageTree(_storageTrieAdapter, storageRoot, logManager);
        _tree.RootHash = storageRoot;

        _warmupStorageTree = new StorageTree(_warmerStorageTrieAdapter, logManager);
        _warmupStorageTree.RootHash = storageRoot;

        _config = config;

        // In case its all write.
        // TODO: Check hint set is working or not.
        _trieCacheWarmer.PushJob(_scope, null, this, 0, _scope.HintSequenceId);
    }

    public Hash256 RootHash => _tree.RootHash;
    public byte[] Get(in UInt256 index)
    {
        if (!_config.ReadWithTrie && TryGet(index, out var value))
        {
            if (value == null) value = State.StorageTree.ZeroBytes;

            if (_config.VerifyWithTrie)
            {
                var treeValue = _tree.Get(index);
                if (!Bytes.AreEqual(treeValue, value))
                {
                    throw new Exception($"Get slot got wrong value. Address {_address}, {_tree.RootHash}, {index}. Tree: {treeValue?.ToHexString()} vs Flat: {value?.ToHexString()}. Self destruct it {_selfDestructKnownStateIdx}");
                }
            }

            HintGet(index, value);
            return value;
        }
        else
        {
            value = _tree.Get(index);
            HintGet(index, value);
            return value;
        }
    }

    public void HintGet(in UInt256 index, byte[]? value)
    {
        // Note: VERY hot code.
        // 90% of the read goes through prewarmer, not actually go through this class, meaning this method is called
        // a lot. Unlike with account, setting the setted slot have a measurable net negative impact on performance.
        // Trying to set this value async through trie warmer proved to be hard to pull of and result in random invalid
        // block.
        WarmUpSlot(index);
    }

    public void HintSet(in UInt256 index)
    {
        WarmUpSlot(index);
    }

    private void WarmUpSlot(UInt256 index)
    {
        _trieCacheWarmer.PushJob(_scope, null, this, index, _scope.HintSequenceId);
    }

    // Called by trie warmer.
    public bool WarUpStorageTrie(UInt256 index, int sequenceId)
    {
        if (_scope.HintSequenceId != sequenceId) return false;

        if (_bundle.ShouldPrewarm(_address, index))
        {
            // Note: storage tree root not changed after write batch. Also not cleared. So the result is not correct.
            // this is just to warm up the nodes.
            ValueHash256 key = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(index, key.BytesAsSpan);
            _ = _warmupStorageTree.Get(key.BytesAsSpan, keepChildRef: true);
            return true;
        }

        return false;
    }

    private bool TryGet(in UInt256 index, out byte[]? value)
    {
        return _bundle.TryGetSlot(_address, index, _selfDestructKnownStateIdx, out value);
    }

    public byte[] Get(in ValueHash256 hash)
    {
        throw new Exception("Not supported");
    }

    private void Set(UInt256 slot, byte[] value)
    {
        _bundle.SetChangedSlot(_address, slot, value);
    }

    public void SelfDestruct()
    {
        _bundle.Clear(_address, _addressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);

        // Technically, they wont actually matter as the trie will traverse the existing path anyway and on self destruct
        // it will just get blocked on root, so this is more of an optimization.
        _storageTrieAdapter.SelfDestructKnownStateIdx = _selfDestructKnownStateIdx;
        _warmerStorageTrieAdapter.SelfDestructKnownStateIdx = _selfDestructKnownStateIdx;
    }

    public void CommitTree()
    {
        _tree.Commit();
    }

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch storageTreeBulkWriteBatch =
            new TrieStoreScopeProvider.StorageTreeBulkWriteBatch(
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

        public void Dispose()
        {
            storageTreeBulkWriteBatch.Dispose();
        }
    }
}

