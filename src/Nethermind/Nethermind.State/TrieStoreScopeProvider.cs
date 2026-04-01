// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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

        public void HintGet(Address address, Account? account)
        {
            _loadedAccounts.TryAdd(address, account);
        }

        public void HintBal(BlockAccessList bal) { }

        public Task ReadBalAsync(BlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink sink, CancellationToken cancellationToken)
        {
            // Phase 1: Bulk read all accounts from the state trie
            int accountCount = 0;
            foreach (AccountChanges _ in bal.AccountChanges)
                accountCount++;

            if (accountCount == 0)
                return Task.CompletedTask;

            AccountChanges[] accountChangesList = new AccountChanges[accountCount];
            int idx = 0;
            foreach (AccountChanges ac in bal.AccountChanges)
            {
                accountChangesList[idx] = ac;
                idx++;
            }

            // Precompute keccak hashes and radix sort for disk cache locality
            int[] sortedOrder = RadixSortAddresses(accountChangesList, accountCount);

            Account?[] accounts = new Account?[accountCount];
            Parallel.For(0, accountCount, (i) =>
            {
                int orig = sortedOrder[i];
                Address address = accountChangesList[orig].Address;
                Account? account = _backingStateTree.Get(address);
                accounts[orig] = account;

                _loadedAccounts.TryAdd(address, account);
                sink.OnAccountRead(address, account);
            });

            cancellationToken.ThrowIfCancellationRequested();

            // Phase 2: Per-account bulk read of storage slots
            for (int i = 0; i < accountCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AccountChanges accountChanges = accountChangesList[i];
                Account? account = accounts[i];

                int slotCount = accountChanges.StorageChanges.Count + accountChanges.StorageReads.Count;
                if (slotCount == 0 || account is null)
                    continue;

                Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
                if (storageRoot == Keccak.EmptyTreeHash)
                    continue;

                Address address = accountChanges.Address;
                StorageTree storageTree = _scopeProvider.CreateStorageTree(address, storageRoot);
                _storages[address] = storageTree;

                UInt256[] slotIndices = new UInt256[slotCount];
                int si = 0;

                foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
                    slotIndices[si++] = slotChanges.Slot;

                foreach (StorageRead storageRead in accountChanges.StorageReads)
                    slotIndices[si++] = storageRead.Key;

                // Radix sort storage slot hashes for disk cache locality
                int[] slotOrder = RadixSortStorageSlots(slotIndices, slotCount);

                Parallel.For(0, slotCount, (si2) =>
                {
                    int orig = slotOrder[si2];
                    byte[] decodedValue = storageTree.Get(in slotIndices[orig]);
                    StorageCell cell = new(address, slotIndices[orig]);
                    sink.OnStorageRead(in cell, decodedValue);
                });
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Radix sort addresses by their keccak hash (bytes 0-3) for trie locality.
        /// Returns an index array representing the sorted order.
        /// </summary>
        private static int[] RadixSortAddresses(AccountChanges[] accountChanges, int count)
        {
            ValueHash256[] hashes = new ValueHash256[count];
            for (int i = 0; i < count; i++)
                hashes[i] = KeccakCache.Compute(accountChanges[i].Address.Bytes);

            return RadixSortByHash(hashes, count);
        }

        /// <summary>
        /// Radix sort storage slots by their keccak hash (bytes 0-3) for trie locality.
        /// Returns an index array representing the sorted order.
        /// </summary>
        private static int[] RadixSortStorageSlots(UInt256[] slotIndices, int count)
        {
            ValueHash256[] hashes = new ValueHash256[count];
            for (int i = 0; i < count; i++)
                StorageTree.ComputeKeyWithLookup(in slotIndices[i], ref hashes[i]);

            return RadixSortByHash(hashes, count);
        }

        /// <summary>
        /// LSD radix sort on bytes 0-3 of the hash. Returns sorted index array.
        /// </summary>
        private static int[] RadixSortByHash(ValueHash256[] hashes, int count)
        {
            int[] idx0 = new int[count];
            int[] idx1 = new int[count];
            ValueHash256[] buf = new ValueHash256[count];

            for (int i = 0; i < count; i++)
                idx0[i] = i;

            Span<int> counts = stackalloc int[256];
            bool flipped = false;

            for (int p = 3; p >= 0; p--)
            {
                RadixPassWithIndices(
                    flipped ? buf : hashes,
                    flipped ? hashes : buf,
                    flipped ? idx1 : idx0,
                    flipped ? idx0 : idx1,
                    count, p, counts);
                flipped = !flipped;
            }

            return flipped ? idx1 : idx0;
        }

        private static void RadixPassWithIndices(
            ReadOnlySpan<ValueHash256> hashSrc, Span<ValueHash256> hashDst,
            ReadOnlySpan<int> idxSrc, Span<int> idxDst,
            int len, int byteIndex, Span<int> counts)
        {
            counts.Clear();
            for (int i = 0; i < len; i++)
                counts[hashSrc[i].BytesAsSpan[byteIndex]]++;

            int total = 0;
            for (int b = 0; b < 256; b++)
            {
                int c = counts[b];
                counts[b] = total;
                total += c;
            }

            for (int i = 0; i < len; i++)
            {
                byte key = hashSrc[i].BytesAsSpan[byteIndex];
                int pos = counts[key]++;
                hashDst[pos] = hashSrc[i];
                idxDst[pos] = idxSrc[i];
            }
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
            return new WorldStateWriteBatch(this, estimatedAccountNumber, _logManager.GetClassLogger());
        }

        public void Commit(long blockNumber)
        {
            using var blockCommitter = _scopeProvider._trieStore.BeginBlockCommit(blockNumber);

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
            return new StorageTreeBulkWriteBatch(estimatedEntries, scope.LookupStorageTree(address),
                (address, rootHash) => MarkDirty(address, rootHash), address);
        }

        public void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash)
        {
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));
        }

        public void Dispose()
        {
            while (_dirtyStorageTree.TryDequeue(out (AddressAsKey, Hash256) entry))
            {
                (AddressAsKey key, Hash256 storageRoot) = entry;
                if (!_dirtyAccounts.TryGetValue(key, out var account))
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
        }
    }

    public class StorageTreeBulkWriteBatch(
        int estimatedEntries,
        StorageTree storageTree,
        Action<Address, Hash256> onRootUpdated,
        AddressAsKey address,
        bool commit = false) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        // Slight optimization on small contract as the index hash can be precalculated in some case.
        public const int MIN_ENTRIES_TO_BATCH = 16;

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

            if (_wasSetCalled) throw new InvalidOperationException("Must call clear first in a storage write batch");
            _hasSelfDestruct = true;
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

                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> asRef =
                    new ArrayPoolListRef<PatriciaTree.BulkSetEntry>(_bulkWrite.AsSpan());
                storageTree.BulkSet(asRef);

                _bulkWrite?.Dispose();
            }

            if (hasSet)
            {
                if (commit)
                {
                    storageTree.Commit();
                }
                else
                {
                    storageTree.UpdateRootHash(_bulkWrite?.Count > 64);
                }
                onRootUpdated(address, storageTree.RootHash);
            }
        }
    }

    public class KeyValueWithBatchingBackedCodeDb(IKeyValueStoreWithBatching codeDb) : IWorldStateScopeProvider.ICodeDb
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
