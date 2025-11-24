// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public class FlatScopeProviderScope : IWorldStateScopeProvider.IScope
{
    private StateId _currentStateId;
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IWorldStateScopeProvider.ICodeDb _codeDb;
    private readonly IFlatDiffRepository _flatDiffRepository;
    internal readonly SnapshotBundleStateTrieStore _stateTrieStore;
    private readonly Dictionary<Hash256, StorageSnapshotBundleStateTrieStore> _storages = new();
    private readonly StateTree _stateTree;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private FlatDiffRepository.Configuration _configuration;

    public FlatScopeProviderScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatDiffRepository flatDiffRepository,
        FlatDiffRepository.Configuration configuration,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        _codeDb = codeDb;
        _flatDiffRepository = flatDiffRepository;
        _stateTrieStore = new SnapshotBundleStateTrieStore(snapshotBundle);
        _stateTree = new StateTree(
            _stateTrieStore,
            logManager
        );
        _configuration = configuration;
        _stateTree.RootHash = currentStateId.stateRoot.ToCommitment();
        _logManager = logManager;
        _isReadOnly = isReadOnly;
    }

    public void Dispose()
    {
        _snapshotBundle.Dispose();
    }

    public Hash256 RootHash => _stateTree.RootHash;
    public void UpdateRootHash()
    {
        _stateTree.UpdateRootHash();
    }

    public Account? Get(Address address)
    {
        _snapshotBundle.TryGetAccount(address, out var account);

        if (!_configuration.VerifyWithTrie)
        {
            return account;
        }

        // TODO: To snapshot bundler
        Account? accTrie = _stateTree.Get(address);
        if (accTrie != account)
        {
            Console.Error.WriteLine($"Incorrect account {accTrie} vs {account}");
        }

        return account;
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
            _configuration,
            storageRoot,
            addressHash,
            _logManager);

        _storages[addressHash] = newTrieStore;
        return newTrieStore;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(long blockNumber)
    {
        // Note: These all runs in about 0.4ms. So the little overhead like attempting to sort the tasks
        // may make it worst. Always check on mainnet.
        using ArrayPoolList<Task> commitTask = new ArrayPoolList<Task>(_storages.Count);
        foreach (KeyValuePair<Hash256, StorageSnapshotBundleStateTrieStore> storage in _storages)
        {
            // TODO: Reenable the parallel.
            /*
            if (blockCommitter.TryRequestConcurrencyQuota())
            {
                commitTask.Add(Task.Factory.StartNew((ctx) =>
                {
                    StorageTree st = (StorageTree)ctx;
                    st.Commit();
                    blockCommitter.ReturnConcurrencyQuota();
                }, storage.Value));
            }
            else
            {
                storage.Value.Commit();
            }
            */
            storage.Value._tree.Commit();
        }

        _stateTree.Commit();
        _storages.Clear();


        Snapshot newSnapshot = _snapshotBundle.CollectAndApplyKnownState();
        StateId newStateId = new StateId(blockNumber, RootHash);
        if (!_isReadOnly)
        {
            if (_currentStateId != newStateId) _flatDiffRepository.AddSnapshot(_currentStateId, newStateId, newSnapshot);
        }
        _currentStateId = newStateId;
    }

    internal class WriteBatch(
        FlatScopeProviderScope scope,
        int estimatedAccountCount,
        ILogger logger
    ) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account)
        {
            _dirtyAccounts[key] = account;
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries)
        {
            var storageTreeImpl = scope.CreateStorageTreeImpl(address);
            return new StorageTreeBulkWriteBatch(
                estimatedEntries,
                storageTreeImpl._tree,
                this,
                storageTreeImpl._storageSnapshotBundle,
                address);
        }

        public void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash)
        {
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));
        }

        public void Dispose()
        {
            while (_dirtyStorageTree.TryDequeue(out var entry))
            {
                (AddressAsKey key, Hash256 storageRoot) = entry;
                if (!_dirtyAccounts.TryGetValue(key, out var account)) account = scope.Get(key);
                if (account == null && storageRoot == Keccak.EmptyTreeHash) continue;
                account ??= ThrowNullAccount(key);
                account = account!.WithChangedStorageRoot(storageRoot);
                _dirtyAccounts[key] = account;
                OnAccountUpdated?.Invoke(key, new IWorldStateScopeProvider.AccountUpdated(key, account));
                if (logger.IsTrace) Trace(key, storageRoot, account);
            }

            using (var stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count))
            {
                foreach (var kv in _dirtyAccounts)
                {
                    stateSetter.Set(kv.Key, kv.Value);
                }
            }
            scope._snapshotBundle.ApplyStateChanges(_dirtyAccounts);


            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account)
                => logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");

            [DoesNotReturn, StackTraceHidden]
            static Account ThrowNullAccount(Address address)
                => throw new InvalidOperationException($"Account {address} is null when updating storage hash");
        }
    }

    private class StorageTreeBulkWriteBatch(
        int estimatedEntries,
        StorageTree storageTree,
        WriteBatch worldStateWriteBatch,
        StorageSnapshotBundle storageSnapshotBundle,
        AddressAsKey address
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
                StorageTree.ComputeKeyWithLookup(index, _keyBuff.BytesAsSpan);
                _bulkWrite.Add(StorageTree.CreateBulkSetEntry(_keyBuff, value));
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
                worldStateWriteBatch.MarkDirty(address, storageTree.RootHash);
            }
        }
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
    internal readonly StorageSnapshotBundle _storageSnapshotBundle;
    internal readonly StorageTree _tree;
    private readonly Hash256 _address;
    private readonly FlatDiffRepository.Configuration _config;

    public StorageSnapshotBundleStateTrieStore(
        FlatScopeProviderScope scope,
        StorageSnapshotBundle storageSnapshotBundle,
        FlatDiffRepository.Configuration config,
        Hash256 storageRoot,
        Hash256 address,
        ILogManager logManager)
    {
        _scope = scope;
        _storageSnapshotBundle = storageSnapshotBundle;
        _tree = new StorageTree(this, storageRoot, logManager);
        _tree.RootHash = storageRoot;
        _config = config;
        _address = address;
    }

    public Hash256 RootHash => _tree.RootHash;
    public byte[] Get(in UInt256 index)
    {
        _storageSnapshotBundle.TryGet(index, out var value);
        if (value == null) value = StorageTree.ZeroBytes;

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
        var rlp = _storageSnapshotBundle.TryLoadRlp(path, hash, flags);
        return rlp;
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
