// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NonBlocking;

namespace Nethermind.State;

public class TrieStoreScopeProvider : IWorldStateScopeProvider
{
    private readonly ITrieStore _trieStore;
    private readonly ILogManager _logManager;
    protected StateTree _backingStateTree;
    private readonly KeyValueWithBatchingBackedCodeDb _codeDb;

    public TrieStoreScopeProvider(ITrieStore trieStore, IKeyValueStoreWithBatching codeDb, ILogManager logManager)
    {
        _trieStore = trieStore;
        _logManager = logManager;
        _codeDb = new KeyValueWithBatchingBackedCodeDb(codeDb);

        _backingStateTree = CreateStateTree();
    }

    protected virtual StateTree CreateStateTree()
    {
        return new StateTree(_trieStore.GetTrieStore(null), _logManager);
    }

    public bool HasRoot(BlockHeader? baseBlock)
    {
        return _trieStore.HasRoot(baseBlock?.StateRoot ?? Keccak.EmptyTreeHash);
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        var trieStoreCloser = _trieStore.BeginScope(baseBlock);
        _backingStateTree.RootHash = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;

        return new TrieStoreWorldStateBackendScope(_backingStateTree, this, _codeDb, trieStoreCloser, _logManager);
    }

    protected virtual StorageTree CreateStorageTree(Address address, Hash256 storageRoot)
    {
        return new StorageTree(_trieStore.GetTrieStore(address), storageRoot, _logManager);
    }

    private class TrieStoreWorldStateBackendScope : IWorldStateScopeProvider.IScope
    {
        public void Dispose()
        {
            _trieStoreCloser.Dispose();
            _backingStateTree.RootHash = Keccak.EmptyTreeHash;
            _storages.Clear();
        }

        public Hash256 RootHash => _backingStateTree.RootHash;
        public void UpdateRootHash() => _backingStateTree.UpdateRootHash();

        public Account? Get(Address address)
        {
            ref Account? account = ref CollectionsMarshal.GetValueRefOrAddDefault(_loadedAccounts, address, out bool exists);
            if (!exists)
            {
                account = _backingStateTree.Get(address);
            }

            return account;
        }

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb1;

        internal StateTree _backingStateTree;
        private readonly Dictionary<AddressAsKey, StorageTree> _storages = new();
        private readonly Dictionary<AddressAsKey, Account?> _loadedAccounts = new();
        private readonly TrieStoreScopeProvider _scopeProvider;
        private readonly IWorldStateScopeProvider.ICodeDb _codeDb1;
        private readonly IDisposable _trieStoreCloser;
        private readonly ILogManager _logManager;

        public TrieStoreWorldStateBackendScope(StateTree backingStateTree, TrieStoreScopeProvider scopeProvider, IWorldStateScopeProvider.ICodeDb codeDb, IDisposable trieStoreCloser, ILogManager logManager)
        {
            _backingStateTree = backingStateTree;
            _logManager = logManager;
            _scopeProvider = scopeProvider;
            _codeDb1 = codeDb;
            _trieStoreCloser = trieStoreCloser;
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNumber)
        {
            return new WorldStateWriteBatch(this, estimatedAccountNumber, _logManager.GetClassLogger<WorldStateWriteBatch>());
        }

        public void Commit(long blockNumber)
        {
            using var blockCommitter = _scopeProvider._trieStore.BeginBlockCommit(blockNumber);

            // Note: These all runs in about 0.4ms. So the little overhead like attempting to sort the tasks
            // may make it worst. Always check on mainnet.
            using ArrayPoolList<Task> commitTask = new ArrayPoolList<Task>(_storages.Count);
            foreach (KeyValuePair<AddressAsKey, StorageTree> storage in _storages)
            {
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
            }

            Task.WaitAll(commitTask.AsSpan());
            _backingStateTree.Commit();
            _storages.Clear();
        }

        internal StorageTree LookupStorageTree(Address address)
        {
            if (_storages.TryGetValue(address, out var storageTree))
            {
                return storageTree;
            }

            storageTree = _scopeProvider.CreateStorageTree(address, Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash);
            _storages[address] = storageTree;
            return storageTree;
        }

        public void ClearLoadedAccounts()
        {
            _loadedAccounts.Clear();
        }

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            return LookupStorageTree(address);
        }
    }

    private class WorldStateWriteBatch(
        TrieStoreWorldStateBackendScope scope,
        int estimatedAccountCount,
        ILogger logger) : IWorldStateScopeProvider.IWorldStateWriteBatch
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
            return new StorageTreeBulkWriteBatch(estimatedEntries, scope.LookupStorageTree(address), this, address);
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
                ref Account? account = ref CollectionsMarshal.GetValueRefOrAddDefault(_dirtyAccounts, key, out bool exists);
                if (!exists) account = scope.Get(key) ?? ThrowNullAccount(key);

                account = account!.WithChangedStorageRoot(storageRoot);
                _dirtyAccounts[key] = account;
                OnAccountUpdated?.Invoke(key, new IWorldStateScopeProvider.AccountUpdated(key, account));
                if (logger.IsTrace) Trace(key, storageRoot, account);
            }

            using (var stateSetter = scope._backingStateTree.BeginSet(_dirtyAccounts.Count))
            {
                foreach (var kv in _dirtyAccounts)
                {
                    stateSetter.Set(kv.Key, kv.Value);
                }
            }

            scope.ClearLoadedAccounts();


            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account)
                => logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");

            [DoesNotReturn, StackTraceHidden]
            static Account ThrowNullAccount(Address address)
                => throw new InvalidOperationException($"Account {address} is null when updating storage hash");
        }
    }

    private class StorageTreeBulkWriteBatch(int estimatedEntries, StorageTree storageTree, WorldStateWriteBatch worldStateWriteBatch, AddressAsKey address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private bool _hasSelfDestruct;
        private bool _wasSetCalled = false;

        ArrayPoolList<PatriciaTree.BulkSetEntry> _bulkWrite = new(estimatedEntries);
        private ValueHash256 _keyBuff = new ValueHash256();

        public void Set(in UInt256 index, byte[] value)
        {
            StorageTree.ComputeKeyWithLookup(index, _keyBuff.BytesAsSpan);
            _bulkWrite.Add(StorageTree.CreateBulkSetEntry(_keyBuff, value));
        }

        public void Clear()
        {
            if (_wasSetCalled) throw new InvalidOperationException("Must call clear first in a storage write batch");
            _hasSelfDestruct = false;
        }

        public void Dispose()
        {
            bool hasSet = false;
            if (_hasSelfDestruct)
            {
                hasSet = true;
                storageTree.RootHash = Keccak.EmptyTreeHash;
            }
            storageTree.BulkSet(_bulkWrite);
            if (_bulkWrite.Count > 0) hasSet = true;

            if (hasSet)
            {
                storageTree.UpdateRootHash(_bulkWrite.Count > 64);
                worldStateWriteBatch.MarkDirty(address, storageTree.RootHash);
            }

            _bulkWrite.Dispose();
        }
    }

    private class KeyValueWithBatchingBackedCodeDb(IKeyValueStoreWithBatching codeDb) : IWorldStateScopeProvider.ICodeDb
    {
        public byte[]? GetCode(in ValueHash256 codeHash)
        {
            return codeDb[codeHash.Bytes]?.ToArray();
        }

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite()
        {
            return new CodeSetter(codeDb.StartWriteBatch());
        }

        private class CodeSetter(IWriteBatch writeBatch) : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
            {
                writeBatch.PutSpan(codeHash.Bytes, code);
            }

            public void Dispose()
            {
                writeBatch.Dispose();
            }
        }
    }
}
