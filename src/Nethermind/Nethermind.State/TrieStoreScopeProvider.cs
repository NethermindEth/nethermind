// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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

public class TrieStoreScopeProvider(ITrieStore trieStore, IKeyValueStoreWithBatching codeDb, ILogManager logManager) : IWorldStateScopeProvider
{
    private readonly ITrieStore _trieStore = trieStore;
    private readonly ILogManager _logManager = logManager;
    protected StateTree _backingStateTree;
    private readonly KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb);

    protected virtual StateTree CreateStateTree() => new(_trieStore.GetTrieStore(null), _logManager);

    public bool HasRoot(BlockHeader? baseBlock) => _trieStore.HasRoot(baseBlock?.StateRoot ?? Keccak.EmptyTreeHash);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        IDisposable trieStoreCloser = _trieStore.BeginScope(baseBlock);
        _backingStateTree ??= CreateStateTree();
        _backingStateTree.RootHash = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;

        return new TrieStoreWorldStateBackendScope(_backingStateTree, this, _codeDb, trieStoreCloser, _logManager);
    }

    protected virtual StorageTree CreateStorageTree(Address address, Hash256 storageRoot) => new(_trieStore.GetTrieStore(address), storageRoot, _logManager);

    private class TrieStoreWorldStateBackendScope(StateTree backingStateTree, TrieStoreScopeProvider scopeProvider, IWorldStateScopeProvider.ICodeDb codeDb, IDisposable trieStoreCloser, ILogManager logManager) : IWorldStateScopeProvider.IScope, IUncachedAccountReader, IUncachedStorageTreeProvider
    {
        public void Dispose()
        {
            _trieStoreCloser.Dispose();
            if (!_committed)
            {
                _backingStateTree.RootHash = Keccak.EmptyTreeHash;
            }
            _storages.Clear();
        }

        public Hash256 RootHash => _backingStateTree.RootHash;
        public void UpdateRootHash() => _backingStateTree.UpdateRootHashParallel();

        public Account? Get(Address address)
        {
            ref Account? account = ref CollectionsMarshal.GetValueRefOrAddDefault(_loadedAccounts, address, out bool exists);
            if (!exists)
            {
                account = _backingStateTree.Get(address);
            }

            return account;
        }

        public Account? GetAccountUncached(Address address) => _backingStateTree.Get(address);

        public bool CanReadAccountUncached => true;

        public void HintGet(Address address, Account? account) => _loadedAccounts.TryAdd(address, account);

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb1;

        internal StateTree _backingStateTree = backingStateTree;
        private readonly Dictionary<AddressAsKey, StorageTree> _storages = new();
        private readonly Dictionary<AddressAsKey, Account?> _loadedAccounts = new();
        private readonly TrieStoreScopeProvider _scopeProvider = scopeProvider;
        private readonly IWorldStateScopeProvider.ICodeDb _codeDb1 = codeDb;
        private readonly IDisposable _trieStoreCloser = trieStoreCloser;
        private readonly ILogManager _logManager = logManager;
        private bool _committed;

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNumber) => new WorldStateWriteBatch(this, estimatedAccountNumber, _logManager.GetClassLogger<TrieStoreWorldStateBackendScope>());

        public void Commit(long blockNumber)
        {
            using IBlockCommitter blockCommitter = _scopeProvider._trieStore.BeginBlockCommit(blockNumber);

            // Note: These all runs in about 0.4ms. So the little overhead like attempting to sort the tasks
            // may make it worst. Always check on mainnet.
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
                    }, storage.Value, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));
                }
                else
                {
                    storage.Value.Commit();
                }
            }

            Task.WaitAll(commitTask.AsSpan());
            _backingStateTree.Commit();
            _committed = true;
            _storages.Clear();
        }

        internal StorageTree LookupStorageTree(Address address, bool cacheAccount = true)
        {
            if (_storages.TryGetValue(address, out StorageTree storageTree))
            {
                return storageTree;
            }

            Account? account = cacheAccount ? Get(address) : GetAccountUncached(address);
            storageTree = _scopeProvider.CreateStorageTree(address, account?.StorageRoot ?? Keccak.EmptyTreeHash);
            _storages[address] = storageTree;
            return storageTree;
        }

        public void ClearLoadedAccounts() => _loadedAccounts.Clear();

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => LookupStorageTree(address);

        public IWorldStateScopeProvider.IStorageTree CreateStorageTreeUncachedAccount(Address address)
        {
            Account? account = GetAccountUncached(address);
            return _scopeProvider.CreateStorageTree(address, account?.StorageRoot ?? Keccak.EmptyTreeHash);
        }

        public bool CanCreateStorageTreeUncachedAccount => true;
    }

    private class WorldStateWriteBatch(
        TrieStoreWorldStateBackendScope scope,
        int estimatedAccountCount,
        ILogger logger) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();
        private readonly ConcurrentQueue<StorageRootWorkItem> _pendingStorageRoots = new();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account) => _dirtyAccounts[key] = account;

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries)
        {
            StorageTree storageTree = scope.LookupStorageTree(address);
#if ZK_EVM
            return new StorageTreeBulkWriteBatch(
                estimatedEntries,
                storageTree,
                (address, rootHash) => MarkDirty(address, rootHash),
                address);
#else
            return new StorageTreeBulkWriteBatch(
                estimatedEntries,
                storageTree,
                RegisterStorageRootWork,
                address);
#endif
        }

        public void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash) => _dirtyStorageTree.Enqueue((address, storageTreeRootHash));

        private void RegisterStorageRootWork(StorageRootWorkItem workItem) => _pendingStorageRoots.Enqueue(workItem);

        public void Dispose()
        {
            CompletePendingStorageRoots(_pendingStorageRoots, MarkDirty);

            while (_dirtyStorageTree.TryDequeue(out (AddressAsKey, Hash256) entry))
            {
                (AddressAsKey key, Hash256 storageRoot) = entry;
                if (!_dirtyAccounts.TryGetValue(key, out Account? account))
                    account = scope.Get(key);

                // Account may be null when EIP-161 deletes an empty account that had storage
                // changes in the same block. Skip the storage root update since the account
                // will not exist in the state trie.
                if (account is null) continue;
                account = account.WithChangedStorageRoot(storageRoot);
                _dirtyAccounts[key] = account;
                OnAccountUpdated?.Invoke(key, new IWorldStateScopeProvider.AccountUpdated(key, account));
                if (logger.IsTrace) Trace(key, storageRoot, account);
            }

            using (StateTree.StateTreeBulkSetter stateSetter = scope._backingStateTree.BeginSet(_dirtyAccounts.Count))
            {
                foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                {
                    stateSetter.Set(kv.Key, kv.Value);
                }
            }

            scope.ClearLoadedAccounts();


            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account)
                => logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");
        }

    }

    public readonly struct StorageRootWorkItem(
        AddressAsKey address,
        StorageTree tree,
        int writeCount,
        int estimatedWeight,
        int[]? firstNibbleWeights,
        bool wasCleared,
        bool commit)
    {
        public readonly AddressAsKey Address = address;
        public readonly StorageTree Tree = tree;
        public readonly int WriteCount = writeCount;
        public readonly int EstimatedWeight = estimatedWeight;
        public readonly int[]? FirstNibbleWeights = firstNibbleWeights;
        public readonly bool WasCleared = wasCleared;
        public readonly bool Commit = commit;
    }

    public static void CompletePendingStorageRoots(
        ConcurrentQueue<StorageRootWorkItem> pending,
        Action<AddressAsKey, Hash256> markDirty)
    {
        if (pending.IsEmpty) return;

        using ArrayPoolList<StorageRootWorkItem> storageRoots = new(pending.Count);
        while (pending.TryDequeue(out StorageRootWorkItem workItem)) storageRoots.Add(workItem);

        using ArrayPoolList<TrieRootHashWorkItem> rootHashWork = new(storageRoots.Count);
        for (int i = 0; i < storageRoots.Count; i++)
        {
            ref StorageRootWorkItem item = ref storageRoots.GetRef(i);
            rootHashWork.Add(new TrieRootHashWorkItem(item.Tree, item.EstimatedWeight, item.FirstNibbleWeights));
        }

        PatriciaTree.UpdateRootHashes(rootHashWork.AsSpan());

        for (int i = 0; i < storageRoots.Count; i++)
        {
            ref StorageRootWorkItem item = ref storageRoots.GetRef(i);
            if (item.Commit) item.Tree.Commit();
            markDirty(item.Address, item.Tree.RootHash);
        }
    }

    public class StorageTreeBulkWriteBatch : IWorldStateScopeProvider.IStorageWriteBatch
    {
        // Slight optimization on small contract as the index hash can be precalculated in some case.
        public const int MIN_ENTRIES_TO_BATCH = 16;

        private readonly int _estimatedEntries;
        private readonly StorageTree _storageTree;
        private readonly Action<Address, Hash256>? _onRootUpdated;
        private readonly Action<StorageRootWorkItem>? _registerRootWork;
        private readonly AddressAsKey _address;
        private readonly bool _commit;

        private bool _hasSelfDestruct;
        private bool _wasSetCalled;
        private int _writeCount;
        private int[]? _firstNibbleWeights;

        private ArrayPoolList<PatriciaTree.BulkSetEntry>? _bulkWrite;

        private ValueHash256 _keyBuff;

        public StorageTreeBulkWriteBatch(
            int estimatedEntries,
            StorageTree storageTree,
            Action<Address, Hash256> onRootUpdated,
            AddressAsKey address,
            bool commit = false)
        {
            _estimatedEntries = estimatedEntries;
            _storageTree = storageTree;
            _onRootUpdated = onRootUpdated;
            _address = address;
            _commit = commit;
            _bulkWrite = estimatedEntries > MIN_ENTRIES_TO_BATCH ? new(estimatedEntries) : null;
        }

        public StorageTreeBulkWriteBatch(
            int estimatedEntries,
            StorageTree storageTree,
            Action<StorageRootWorkItem> registerRootWork,
            AddressAsKey address,
            bool commit = false)
        {
            _estimatedEntries = estimatedEntries;
            _storageTree = storageTree;
            _registerRootWork = registerRootWork;
            _address = address;
            _commit = commit;
            _bulkWrite = estimatedEntries > MIN_ENTRIES_TO_BATCH ? new(estimatedEntries) : null;
        }

        public void Set(in UInt256 index, byte[] value)
        {
            _wasSetCalled = true;
            _writeCount++;
            StorageTree.ComputeKeyWithLookup(index, ref _keyBuff);
            if (_bulkWrite is null)
            {
                _storageTree.Set(in _keyBuff, value);
            }
            else
            {
                if (_registerRootWork is not null)
                {
                    RecordFirstNibbleWeight();
                }
                _bulkWrite.Add(StorageTree.CreateBulkSetEntry(_keyBuff, value));
            }
        }

        public void Clear()
        {
            if (_bulkWrite is null)
            {
                _storageTree.RootHash = Keccak.EmptyTreeHash;
            }

            if (_wasSetCalled) throw new InvalidOperationException("Must call clear first in a storage write batch");
            _hasSelfDestruct = true;
        }

        public void Dispose()
        {
            bool hasSet = _wasSetCalled || _hasSelfDestruct;
            int bulkCount = 0;
            if (_bulkWrite is not null)
            {
                if (_hasSelfDestruct)
                {
                    _storageTree.RootHash = Keccak.EmptyTreeHash;
                }

                bulkCount = _bulkWrite.Count;
                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> asRef = _bulkWrite.ToRef();
                PatriciaTree.Flags flags = _registerRootWork is null ? PatriciaTree.Flags.None : PatriciaTree.Flags.DoNotParallelize;
                _storageTree.BulkSet(asRef, flags);
            }

            if (hasSet)
            {
                if (_registerRootWork is not null)
                {
                    int actualWrites = Math.Max(1, _writeCount);
                    _registerRootWork(new StorageRootWorkItem(
                        _address,
                        _storageTree,
                        actualWrites,
                        Math.Max(actualWrites, _estimatedEntries),
                        _firstNibbleWeights,
                        _hasSelfDestruct,
                        _commit));
                    return;
                }

                if (_commit)
                {
                    _storageTree.Commit();
                }
                else
                {
                    _storageTree.UpdateRootHash(bulkCount > 64);
                }

                _onRootUpdated!(_address, _storageTree.RootHash);
            }
        }

        private void RecordFirstNibbleWeight()
        {
            _firstNibbleWeights ??= new int[16];
            _firstNibbleWeights[(_keyBuff.BytesAsSpan[0] & 0xf0) >> 4]++;
        }
    }

    public class KeyValueWithBatchingBackedCodeDb(IKeyValueStoreWithBatching codeDb) : IWorldStateScopeProvider.ICodeDb
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
