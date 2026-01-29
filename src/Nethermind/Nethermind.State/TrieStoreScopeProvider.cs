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

namespace Nethermind.State;

public class TrieStoreScopeProvider : IWorldStateScopeProvider
{
    private readonly ITrieStore _trieStore;
    private readonly ILogManager _logManager;
    private readonly KeyValueWithBatchingBackedCodeDb _codeDb;

    public TrieStoreScopeProvider(ITrieStore trieStore, IKeyValueStoreWithBatching codeDb, ILogManager logManager)
    {
        _trieStore = trieStore;
        _logManager = logManager;
        _codeDb = new KeyValueWithBatchingBackedCodeDb(codeDb);

        BackingStateTree = CreateStateTree();
    }

    protected StateTree BackingStateTree { get; }

    protected virtual StateTree CreateStateTree() => new(_trieStore.GetTrieStore(null), _logManager);

    public bool HasRoot(BlockHeader? baseBlock) => _trieStore.HasRoot(baseBlock?.StateRoot ?? Keccak.EmptyTreeHash);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        IDisposable trieStoreCloser = _trieStore.BeginScope(baseBlock);
        BackingStateTree.RootHash = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;

        return new TrieStoreWorldStateBackendScope(BackingStateTree, this, _codeDb, trieStoreCloser, _logManager);
    }

    protected virtual StorageTree CreateStorageTree(Address address, Hash256 storageRoot) => new(_trieStore.GetTrieStore(address), storageRoot, _logManager);

    private class TrieStoreWorldStateBackendScope(
        StateTree backingStateTree,
        TrieStoreScopeProvider scopeProvider,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IDisposable trieStoreCloser,
        ILogManager logManager)
        : IWorldStateScopeProvider.IScope
    {
        public void Dispose()
        {
            trieStoreCloser.Dispose();
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

        public void HintGet(Address address, Account? account) => _loadedAccounts.TryAdd(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => codeDb;

        internal readonly StateTree _backingStateTree = backingStateTree;
        private readonly Dictionary<AddressAsKey, StorageTree> _storages = new();
        private readonly Dictionary<AddressAsKey, Account?> _loadedAccounts = new();

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNumber) =>
            new WorldStateWriteBatch(this, estimatedAccountNumber, logManager.GetClassLogger<WorldStateWriteBatch>());

        public void Commit(long blockNumber)
        {
            using IBlockCommitter blockCommitter = scopeProvider._trieStore.BeginBlockCommit(blockNumber);

            // Note: These all run in about 0.4ms. So the little overhead like attempting to sort the tasks
            // may make it worst. Always check on the mainnet.
            using ArrayPoolListRef<Task> commitTask = new(_storages.Count);
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
            if (!_storages.TryGetValue(address, out StorageTree storageTree))
            {
                storageTree = _storages[address] = scopeProvider.CreateStorageTree(address, Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash);
            }

            return storageTree;
        }

        public void ClearLoadedAccounts() => _loadedAccounts.Clear();

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => LookupStorageTree(address);
    }

    private class WorldStateWriteBatch(
        TrieStoreWorldStateBackendScope scope,
        int estimatedAccountCount,
        ILogger logger) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account) => _dirtyAccounts[key] = account;

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries) =>
            new StorageTreeBulkWriteBatch(estimatedEntries, scope.LookupStorageTree(address), this, address);

        public void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash) =>
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));

        public void Dispose()
        {
            EventHandler<IWorldStateScopeProvider.AccountUpdated> onAccountUpdated = OnAccountUpdated;

            while (_dirtyStorageTree.TryDequeue(out (AddressAsKey, Hash256) entry))
            {
                (AddressAsKey key, Hash256 storageRoot) = entry;
                if (!_dirtyAccounts.TryGetValue(key, out Account? account)) account = scope.Get(key);
                if (account is null && storageRoot == Keccak.EmptyTreeHash) continue;
                account ??= ThrowNullAccount(key);
                account = account!.WithChangedStorageRoot(storageRoot);
                _dirtyAccounts[key] = account;
                onAccountUpdated?.Invoke(key, new IWorldStateScopeProvider.AccountUpdated(key, account));
                if (logger.IsTrace) Trace(key, storageRoot, account);
            }

            using (StateTree.StateTreeBulkSetter? stateSetter = scope._backingStateTree.BeginSet(_dirtyAccounts.Count))
            {
                foreach (KeyValuePair<AddressAsKey, Account> kv in _dirtyAccounts)
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
        // Slight optimization on a small contract as the index hash can be precalculated in some case.
        private const int MIN_ENTRIES_TO_BATCH = 16;

        private bool _hasSelfDestruct;
        private bool _wasSetCalled;

        private readonly ArrayPoolList<PatriciaTree.BulkSetEntry>? _bulkWrite = estimatedEntries > MIN_ENTRIES_TO_BATCH
            ? new(estimatedEntries)
            : null;

        private ValueHash256 _keyBuff;

        public void Set(in UInt256 index, byte[] value)
        {
            _wasSetCalled = true;
            if (_bulkWrite is null)
            {
                storageTree.Set(index, value);
            }
            else
            {
                StorageTree.ComputeKeyWithLookup(index, ref _keyBuff);
                _bulkWrite.Add(StorageTree.CreateBulkSetEntry(_keyBuff, value));
            }
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
        }

        public void Dispose()
        {
            bool hasSet = _wasSetCalled || _hasSelfDestruct;
            if (_bulkWrite is not null)
            {
                if (_hasSelfDestruct)
                {
                    storageTree.RootHash = Keccak.EmptyTreeHash;
                }

                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> asRef = new(_bulkWrite.AsSpan());
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

    private class KeyValueWithBatchingBackedCodeDb(IKeyValueStoreWithBatching codeDb) : IWorldStateScopeProvider.ICodeDb
    {
        public byte[]? GetCode(in ValueHash256 codeHash) => codeDb[codeHash.Bytes]?.ToArray();
        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new CodeSetter(codeDb.StartWriteBatch());

        private class CodeSetter(IWriteBatch writeBatch) : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) => writeBatch.PutSpan(codeHash.Bytes, code);
            public void Dispose() => writeBatch.Dispose();
        }
    }
}
