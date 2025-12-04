// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NonBlocking;

namespace Nethermind.State.Flat.ScopeProvider;

public class StorageTree : IWorldStateScopeProvider.IStorageTree
{
    private readonly State.StorageTree _tree;
    private readonly State.StorageTree _warmupStorageTree;
    private readonly Address _address;
    private readonly FlatDiffRepository.Configuration _config;
    private readonly ITrieStoreTrieCacheWarmer _trieCacheWarmer;
    private readonly WorldStateScope _scope;
    private readonly SnapshotBundle _bundle;
    private readonly Hash256 _addressHash;
    private int _selfDestructKnownStateIdx;

    public StorageTree(
        WorldStateScope scope,
        ITrieStoreTrieCacheWarmer trieCacheWarmer,
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
        _selfDestructKnownStateIdx = bundle.DetermineSelfDestructStateIdx(address);

        _tree = new State.StorageTree(
            new TrieStoreAdapter(this, concurrencyQuota, isTrieWarmer: false),
            storageRoot, logManager);
        _tree.RootHash = storageRoot;
        _warmupStorageTree = new State.StorageTree(
            new TrieStoreAdapter(this, concurrencyQuota, isTrieWarmer: true),
            logManager);
        _warmupStorageTree.RootHash = storageRoot;

        _config = config;

        // In case its all write.
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
        if (HintSequenceId != sequenceId) return false;

        if (ShouldPrewarm(index))
        {
            // Note: storage tree root not changed after write batch. Also not cleared. So the result is not correct.
            _ = _warmupStorageTree.Get(index);
            MaybePreReadSlot(index, sequenceId);
        }

        return true;
    }

    public int HintSequenceId => _bundle.HintSequenceId;

    public bool TryGet(in UInt256 index, out byte[]? value)
    {
        return _bundle.TryGetSlot(_address, index, _selfDestructKnownStateIdx, out value);
    }

    public bool TryFindNode(in TreePath path, Hash256 hash, out TrieNode value)
    {
        return _bundle.TryFindNode(_addressHash, path, hash, _selfDestructKnownStateIdx, out value);
    }

    public bool ShouldPrewarm(UInt256 index)
    {
        return _bundle.ShouldPrewarm(_address, index);
    }

    public void MaybePreReadSlot(UInt256 slot, int sequenceId)
    {
        _bundle.MaybePreReadSlot(_address, slot, sequenceId);
    }

    public byte[] Get(in ValueHash256 hash)
    {
        throw new Exception("Not supported");
    }

    private byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags, bool isTrieWarmer)
    {
        return _bundle.TryLoadRlp(_addressHash, in path, hash, flags, isTrieWarmer);
    }

    public void Set(UInt256 slot, byte[] value)
    {
        _bundle.SetChangedSlot(_address, slot, value);
    }

    public void SelfDestruct()
    {
        _bundle.Clear(_address, _addressHash);
        _selfDestructKnownStateIdx = _bundle.GetSelfDestructKnownStateId();
    }

    public void SetNode(TreePath path, TrieNode node)
    {
        _bundle.SetNode(_addressHash, path, node);
    }

    public void SetNodeHint(in TreePath path, TrieNode node)
    {
        _bundle.HintTrieNode(_addressHash, path, node);
    }


    public void CommitTree()
    {
        _tree.Commit();
    }

    private class TrieStoreAdapter(
        StorageTree storageTree,
        ConcurrencyQuota concurrencyQuota,
        bool isTrieWarmer
    ): AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            if (storageTree.TryFindNode(path, hash, out var node))
            {
                return node;
            }

            TrieNode newNode = new TrieNode(NodeType.Unknown, hash);
            if (isTrieWarmer)
            {
                if (hash is not null) storageTree.SetNodeHint(path, newNode);
            }
            else
            {
                storageTree.SetNode(path, newNode);
            }
            return newNode;
        }

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            return storageTree.TryLoadRlp(path, hash, flags, isTrieWarmer);
        }

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(storageTree, concurrencyQuota);
    }

    private class Committer(StorageTree storageTree, ConcurrencyQuota concurrencyQuota) : AbstractMinimalTrieStore.AbstractMinimalCommitter(concurrencyQuota)
    {
        public override TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            storageTree.SetNode(path, node);
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
            this
        );
    }

    private class StorageTreeBulkWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch storageTreeBulkWriteBatch,
        StorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
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
