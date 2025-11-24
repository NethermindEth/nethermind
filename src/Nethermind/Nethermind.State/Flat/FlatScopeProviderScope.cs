// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public class FlatScopeProviderScope : IWorldStateScopeProvider.IScope
{
    private readonly StateId _currentStateId;
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IWorldStateScopeProvider.ICodeDb _codeDb;
    internal readonly SnapshotBundleStateTrieStore _stateTrieStore;
    private readonly Dictionary<Hash256, StorageSnapshotBundleStateTrieStore> _storages = new();
    private readonly StateTree _stateTree;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;

    public FlatScopeProviderScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        _codeDb = codeDb;
        _stateTrieStore = new SnapshotBundleStateTrieStore(snapshotBundle);
        _stateTree = new StateTree(
            _stateTrieStore,
            logManager
        );
        _stateTree.RootHash = currentStateId.stateRoot.ToCommitment();
        _logManager = logManager;
        _isReadOnly = isReadOnly;
    }

    public void Dispose()
    {
        _snapshotBundle.Dispose();
    }

    public Hash256 RootHash => _currentStateId.stateRoot.ToHash256();
    public void UpdateRootHash()
    {
        _stateTree.UpdateRootHash();
    }

    public Account? Get(Address address)
    {
        // TODO: To snapshot bundler
        return _stateTree.Get(address);
    }

    public void HintGet(Address address, Account? account)
    {
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;
    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
    {
        return CreateStorageTreeImpl(address);
    }

    public StorageSnapshotBundleStateTrieStore CreateStorageTreeImpl(Address address)
    {
        Hash256 addressHash = address.ToAccountPath.ToHash256();
        if (_storages.TryGetValue(addressHash, out var storage))
        {
            return storage;
        }

        Hash256 storageRoot = Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        StorageSnapshotBundleStateTrieStore newTrieStore = new StorageSnapshotBundleStateTrieStore(
            this,
            _snapshotBundle.GatherStorageCache(address),
            storageRoot,
            _logManager);

        _storages[addressHash] = newTrieStore;
        return newTrieStore;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        throw new NotImplementedException();
    }

    public void Commit(long blockNumber)
    {
        if (_isReadOnly) return;
        throw new NotImplementedException();
    }
}

public class SnapshotBundleStateTrieStore(
    SnapshotBundle bundle
) : IScopedTrieStore
{
    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        if (bundle.TryFindNode(path, out var node))
        {
            return node;
        }

        TrieNode newNode = new TrieNode(NodeType.Unknown, hash);
        bundle.SetStateNode(path, newNode);
        return newNode;
    }

    private void SetNode(TreePath path, TrieNode node)
    {
        bundle.SetStateNode(path, node);
    }

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? value = TryLoadRlp(path, hash, flags);
        if (value is null)
        {
            throw new TrieNodeException($"Missing trie node. {path}:{hash}", path, hash);
        }

        return value;
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        return bundle.TryLoadRlp(path, hash, flags);
    }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address == null) return this;
        throw new Exception("Should not happen");
    }

    public INodeStorage.KeyScheme Scheme { get; }
    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        return new Committer(this);
    }

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        throw new Exception("Is persisted check not supported");
    }

    public class Committer(SnapshotBundleStateTrieStore stateTrieStore) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            stateTrieStore.SetNode(path, node);
            return node;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");

        public void Dispose()
        {
        }
    }
}

public class StorageSnapshotBundleStateTrieStore : IScopedTrieStore, IWorldStateScopeProvider.IStorageTree
{
    private readonly FlatScopeProviderScope _scope;
    private readonly StorageSnapshotBundle _storageSnapshotBundle;
    private readonly StorageTree _tree;

    public StorageSnapshotBundleStateTrieStore(
        FlatScopeProviderScope scope,
        StorageSnapshotBundle storageSnapshotBundle,
        Hash256 storageRoot,
        ILogManager logManager)
    {
        _scope = scope;
        _storageSnapshotBundle = storageSnapshotBundle;
        _tree = new StorageTree(this, storageRoot, logManager);
    }

    public Hash256 RootHash { get; }
    public byte[] Get(in UInt256 index)
    {
        return _tree.Get(index);
    }

    public void HintGet(in UInt256 index, byte[]? value)
    {
    }

    public byte[] Get(in ValueHash256 hash)
    {
        throw new Exception("Not supported");
    }

    public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
    {
        if (_storageSnapshotBundle.TryFindNode(path, out var node))
        {
            return node;
        }

        TrieNode newNode = new TrieNode(NodeType.Unknown, hash);
        _storageSnapshotBundle.SetNode(path, newNode);
        return newNode;
    }

    public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[]? value = TryLoadRlp(path, hash, flags);
        if (value is null)
        {
            throw new TrieNodeException($"Missing trie node. {path}:{hash}", path, hash);
        }

        return value;
    }

    public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        return _storageSnapshotBundle.TryLoadRlp(path, hash, flags);
    }

    public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
    {
        if (address == _storageSnapshotBundle._addressHash) return this;
        if (address == null) return _scope._stateTrieStore;
        throw new Exception("Should not happen");
    }

    public INodeStorage.KeyScheme Scheme { get; }
    public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        return new Committer(this);
    }

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        throw new Exception("Is persisted check not supported");
    }

    private void SetNode(TreePath path, TrieNode node)
    {
        _storageSnapshotBundle.SetNode(path, node);
    }

    public class Committer(StorageSnapshotBundleStateTrieStore stateTrieStore) : ICommitter
    {
        public TrieNode CommitNode(ref TreePath path, TrieNode node)
        {
            stateTrieStore.SetNode(path, node);
            return node;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowUnknownHash(TrieNode node) => throw new TrieStoreException($"The hash of {node} should be known at the time of committing.");

        public void Dispose()
        {
        }
    }
}


