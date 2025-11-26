// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

public class StorageTree : IWorldStateScopeProvider.IStorageTree
{
    private readonly StorageSnapshotBundle _storageSnapshotBundle;
    internal readonly State.StorageTree _tree;
    private readonly Address _address;
    private readonly FlatDiffRepository.Configuration _config;

    public StorageTree(
        StorageSnapshotBundle storageSnapshotBundle,
        FlatDiffRepository.Configuration config,
        ConcurrencyQuota concurrencyQuota,
        Hash256 storageRoot,
        Address address,
        ILogManager logManager)
    {
        _storageSnapshotBundle = storageSnapshotBundle;
        _tree = new State.StorageTree(new TrieStoreAdapter(storageSnapshotBundle, concurrencyQuota), storageRoot, logManager);
        _tree.RootHash = storageRoot;
        _config = config;
        _address = address;
    }

    public Hash256 RootHash => _tree.RootHash;
    public byte[] Get(in UInt256 index)
    {
        if (_config.ReadWithTrie)
        {
            return _tree.Get(index);
        }

        _storageSnapshotBundle.TryGet(index, out var value);
        if (value == null) value = State.StorageTree.ZeroBytes;

        _storageSnapshotBundle.Set(index, value);

        if (!_config.VerifyWithTrie)
        {
            return value;
        }

        var treeValue = _tree.Get(index);
        if (!Bytes.AreEqual(treeValue, value))
        {
            Console.Error.WriteLine($"Get slot got wrong value. Address {_address}, {_tree.RootHash}, {index} {treeValue?.ToHexString()} vs {value?.ToHexString()}");
        }
        return treeValue;
    }

    public void HintGet(in UInt256 index, byte[]? value)
    {
        _storageSnapshotBundle.Set(index, value);
    }

    public byte[] Get(in ValueHash256 hash)
    {
        throw new Exception("Not supported");
    }

    public void CommitTree()
    {
        _tree.Commit();
    }

    private class TrieStoreAdapter(StorageSnapshotBundle storageSnapshotBundle, ConcurrencyQuota concurrencyQuota): AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            if (storageSnapshotBundle.TryFindNode(path, hash, out var node))
            {
                return node;
            }

            TrieNode newNode = new TrieNode(NodeType.Unknown, hash);
            storageSnapshotBundle.SetNode(path, newNode);
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
        return new StorageTreeBulkWriteBatch(
            estimatedEntries,
            this._tree,
            onRootUpdated,
            storageSnapshotBundle: _storageSnapshotBundle,
            _address
        );
    }

    private class StorageTreeBulkWriteBatch(
        int estimatedEntries,
        State.StorageTree storageTree,
        Action<Address, Hash256> onRootUpdated,
        StorageSnapshotBundle storageSnapshotBundle,
        Address address
    ) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        // Slight optimization on small contract as the index hash can be precalculated in some case.
        private const int MIN_ENTRIES_TO_BATCH = 16;

        private bool _hasSelfDestruct;
        private bool _wasSetCalled = false;

        private ArrayPoolList<PatriciaTree.BulkSetEntry>? _bulkWrite =
            estimatedEntries > MIN_ENTRIES_TO_BATCH
                ? new(estimatedEntries)
                : null;

        private ValueHash256 _keyBuff = new ValueHash256();

        public void Set(in UInt256 index, byte[] value)
        {
            _wasSetCalled = true;
            if (_bulkWrite is null)
            {
                storageTree.Set(index, value);
            }
            else
            {
                State.StorageTree.ComputeKeyWithLookup(index, _keyBuff.BytesAsSpan);
                _bulkWrite.Add(State.StorageTree.CreateBulkSetEntry(_keyBuff, value));
            }
            storageSnapshotBundle.Set(index, value);
        }

        public void Clear()
        {
            if (_bulkWrite is null)
            {
                storageTree.RootHash = Keccak.EmptyTreeHash;
            }
            else
            {
                if (_wasSetCalled) throw new InvalidOperationException("Must call clear first in a storage write batch");
                _hasSelfDestruct = true;
            }

            storageSnapshotBundle.SelfDestruct();
        }

        public void Dispose()
        {
            bool hasSet = (_wasSetCalled || _hasSelfDestruct);
            if (_bulkWrite is not null)
            {
                if (_hasSelfDestruct)
                {
                    storageTree.RootHash = Keccak.EmptyTreeHash;
                }

                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> asRef =
                    new ArrayPoolListRef<PatriciaTree.BulkSetEntry>(_bulkWrite.AsSpan());
                storageTree.BulkSet(asRef);

                _bulkWrite?.Dispose();
            }

            if (hasSet)
            {
                storageTree.UpdateRootHash(_bulkWrite?.Count > 64);
                onRootUpdated(address, storageTree.RootHash);
            }
        }
    }
}
