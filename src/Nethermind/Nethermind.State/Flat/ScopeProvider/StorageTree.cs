// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NonBlocking;

namespace Nethermind.State.Flat.ScopeProvider;

public class StorageTree : IWorldStateScopeProvider.IStorageTree
{
    private readonly StorageSnapshotBundle _storageSnapshotBundle;
    private readonly State.StorageTree _tree;
    private readonly State.StorageTree _warmupStorageTree;
    private readonly Address _address;
    private readonly FlatDiffRepository.Configuration _config;
    private readonly ITrieStoreTrieCacheWarmer _trieCacheWarmer;
    private readonly WorldStateScope _scope;

    public StorageTree(
        WorldStateScope scope,
        ITrieStoreTrieCacheWarmer trieCacheWarmer,
        StorageSnapshotBundle storageSnapshotBundle,
        FlatDiffRepository.Configuration config,
        ConcurrencyQuota concurrencyQuota,
        Hash256 storageRoot,
        Address address,
        ILogManager logManager)
    {
        _scope = scope;
        _trieCacheWarmer = trieCacheWarmer;
        _storageSnapshotBundle = storageSnapshotBundle;
        _tree = new State.StorageTree(
            new TrieStoreAdapter(storageSnapshotBundle, concurrencyQuota, isTrieWarmer: false),
            storageRoot, logManager);
        _tree.RootHash = storageRoot;
        _warmupStorageTree = new State.StorageTree(
            new TrieStoreAdapter(storageSnapshotBundle, concurrencyQuota, isTrieWarmer: true),
            logManager);
        _warmupStorageTree.RootHash = storageRoot;
        _config = config;
        _address = address;

        // In case its all write.
        _trieCacheWarmer.PushJob(_scope, null, this, 0, _scope.HintSequenceId);
    }

    public Hash256 RootHash => _tree.RootHash;
    public byte[] Get(in UInt256 index)
    {
        if (!_config.ReadWithTrie && _storageSnapshotBundle.TryGet(index, out var value))
        {
            if (value == null) value = State.StorageTree.ZeroBytes;

            if (_config.VerifyWithTrie)
            {
                var treeValue = _tree.Get(index);
                if (!Bytes.AreEqual(treeValue, value))
                {
                    throw new Exception($"Get slot got wrong value. Address {_address}, {_tree.RootHash}, {index} {treeValue?.ToHexString()} vs {value?.ToHexString()}");
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
        WarmUpSlot(index);
    }

    public void HintSet(UInt256 index)
    {
        WarmUpSlot(index);
    }

    private void WarmUpSlot(UInt256 index)
    {
        _trieCacheWarmer.PushJob(_scope, null, this, index, _scope.HintSequenceId);
    }

    public bool WarUpStorageTrie(UInt256 index, int sequenceId)
    {
        if (_storageSnapshotBundle.HintSequenceId != sequenceId) return false;

        if (_storageSnapshotBundle.ShouldPrewarm(index))
        {
            // Note: storage tree root not changed after write batch. Also not cleared. So the result is not correct.
            _ = _warmupStorageTree.Get(index);
            _storageSnapshotBundle.MaybePreReadSlot(index, sequenceId);
        }

        return true;
    }

    public byte[] Get(in ValueHash256 hash)
    {
        throw new Exception("Not supported");
    }

    public void CommitTree()
    {
        _tree.Commit();
    }

    private class TrieStoreAdapter(
        StorageSnapshotBundle storageSnapshotBundle,
        ConcurrencyQuota concurrencyQuota,
        bool isTrieWarmer
    ): AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            if (storageSnapshotBundle.TryFindNode(path, hash, out var node))
            {
                return node;
            }

            TrieNode newNode = new TrieNode(NodeType.Unknown, hash);
            if (isTrieWarmer)
            {
                if (hash is not null) storageSnapshotBundle.SetNodeHint(path, newNode);
            }
            else
            {
                storageSnapshotBundle.SetNode(path, newNode);
            }
            return newNode;
        }

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            var rlp = storageSnapshotBundle.TryLoadRlp(path, hash, flags);
            return rlp;
        }

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(storageSnapshotBundle, concurrencyQuota);
    }

    private class Committer(StorageSnapshotBundle snapshotBundle, ConcurrencyQuota concurrencyQuota) : AbstractMinimalTrieStore.AbstractMinimalCommitter(concurrencyQuota)
    {
        public override TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            snapshotBundle.SetNode(path, node);
            return node;
        }
    }

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch storageTreeBulkWriteBatch =
            new TrieStoreScopeProvider.StorageTreeBulkWriteBatch(
                estimatedEntries,
                _tree,
                onRootUpdated,
                _address);

        return new StorageTreeBulkWriteBatch(
            storageTreeBulkWriteBatch,
            _storageSnapshotBundle
        );
    }

    private class StorageTreeBulkWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch storageTreeBulkWriteBatch,
        StorageSnapshotBundle storageSnapshotBundle) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            storageTreeBulkWriteBatch.Set(in index, value);
            storageSnapshotBundle.Set(index, value);
        }

        public void Clear()
        {
            storageTreeBulkWriteBatch.Clear();
            storageSnapshotBundle.SelfDestruct();
        }

        public void Dispose()
        {
            storageTreeBulkWriteBatch.Dispose();
        }
    }
}
