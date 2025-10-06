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
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NonBlocking;
using Prometheus;

namespace Nethermind.State;

public class TrieStoreScopeProvider : IWorldStateScopeProvider
{
    private readonly ITrieStore _trieStore;
    private readonly ILogManager _logManager;
    protected StateTree _backingStateTree;
    private readonly KeyValueWithBatchingBackedCodeDb _codeDb;

    private readonly IProcessExitSource _exitSource;
    private bool _isPrimary = false;

    public TrieStoreScopeProvider(ITrieStore trieStore, IKeyValueStoreWithBatching codeDb, IProcessExitSource exitSource, bool isPrimary, ILogManager logManager)
    {
        _trieStore = trieStore;
        _logManager = logManager;
        _codeDb = new KeyValueWithBatchingBackedCodeDb(codeDb);
        _exitSource = exitSource;
        _isPrimary = isPrimary;

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
        var baseRootHash = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;
        _backingStateTree.RootHash = baseRootHash;

        return new TrieStoreWorldStateBackendScope(
            _backingStateTree,
            this,
            _codeDb,
            trieStoreCloser,
            _exitSource,
            _isPrimary,
            _logManager);
    }

    protected virtual StorageTree CreateStorageTree(Address address, Hash256 storageRoot)
    {
        return new StorageTree(_trieStore.GetTrieStore(address), storageRoot, _logManager);
    }

    private class TrieStoreWorldStateBackendScope : IWorldStateScopeProvider.IScope
    {

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb1;

        internal StateTree _backingStateTree;
        private readonly Dictionary<AddressAsKey, StorageTree> _storages = new();
        private readonly Dictionary<AddressAsKey, Account?> _loadedAccounts = new();
        private readonly TrieStoreScopeProvider _scopeProvider;
        private readonly IWorldStateScopeProvider.ICodeDb _codeDb1;
        private readonly IDisposable _trieStoreCloser;
        private readonly ILogManager _logManager;
        private bool _commitReached;
        private bool _isPrimary = false;
        private string _removedStack = "";

        private static Counter _trieWarmEr = Prometheus.Metrics.CreateCounter("triestore_trie_warmer", "hit rate", "type");

        private ChannelWriter<(Address, UInt256?)>? _trieWarmerJobs;
        Task? _warmerJob = null;
        public TrieStoreWorldStateBackendScope(
            StateTree backingStateTree,
            TrieStoreScopeProvider scopeProvider,
            IWorldStateScopeProvider.ICodeDb codeDb,
            IDisposable trieStoreCloser,
            IProcessExitSource processExitSource,
            bool isPrimary,
            ILogManager logManager
        )
        {
            _backingStateTree = backingStateTree;
            _logManager = logManager;
            _scopeProvider = scopeProvider;
            _codeDb1 = codeDb;
            _trieStoreCloser = trieStoreCloser;

            if (isPrimary)
            {
                _isPrimary = isPrimary;

                var chan = Channel.CreateBounded<(Address, UInt256?)>(new BoundedChannelOptions(1024)
                {
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropWrite
                });

                _trieWarmerJobs = chan.Writer;
                _warmerJob = Task.Run<Task>(async () =>
                {
                    int completed = 0;
                    ArrayPoolList<Task> tasks = new ArrayPoolList<Task>(Environment.ProcessorCount);
                    for (int i = 0; i < Environment.ProcessorCount; i++)
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var reader = chan?.Reader;
                                if (reader is null)
                                {
                                    Console.Error.WriteLine("Reader is null");
                                    return;
                                }

                                await foreach (var job in reader.ReadAllAsync(processExitSource.Token))
                                {
                                    (Address address, UInt256? path) = job;

                                    if (_commitReached)
                                    {
                                        if (path is null)
                                        {
                                            _trieWarmEr.WithLabels("state_skip").Inc();
                                        }
                                        else
                                        {
                                            _trieWarmEr.WithLabels("storage_skip").Inc();
                                        }

                                        continue;
                                    }

                                    if (path is null)
                                    {
                                        _backingStateTree.Get(address.ToAccountPath.ToCommitment());
                                        _trieWarmEr.WithLabels("state").Inc();
                                    }
                                    else
                                    {
                                        LookupStorageTree(address).Get(path.Value);
                                        _trieWarmEr.WithLabels("storage").Inc();
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine(ex);
                                throw;
                            }
                            finally
                            {
                                completed++;
                            }
                        }));
                    }

                    await Task.WhenAll(tasks);;
                    _trieWarmerJobs = null;
                    _removedStack = _removedStack + $"in task {completed}";
                });
            }
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNumber)
        {
            _removedStack += "write batch";
            _trieWarmerJobs?.TryComplete();
            return new WorldStateWriteBatch(this, estimatedAccountNumber, _logManager.GetClassLogger<WorldStateWriteBatch>());
        }

        public void Commit(long blockNumber)
        {
            if (_trieWarmerJobs is not null)
            {
                _removedStack += $"commit {Environment.StackTrace}";
            }
            _trieWarmerJobs?.TryComplete();
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
            _commitReached = true; // At the end, then we stop it
            _warmerJob?.Wait();
            _storages.Clear();
        }

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

        public void HintAccountRead(Address address, Account? account)
        {
            _trieWarmerJobs?.TryWrite((address, null));
            _loadedAccounts[address] = account;
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
            var storageTree = LookupStorageTree(address);
            var writer = _trieWarmerJobs;
            if (writer is null)
            {
                return storageTree;
            }
            return new StorageTreeHintWrapper(storageTree, writer, address);
        }
    }

    private class WorldStateWriteBatch(
        TrieStoreWorldStateBackendScope scope,
        int estimatedAccountCount,
        ILogger logger) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();

        public void Set(Address key, Account? account)
        {
            _dirtyAccounts[key] = account;
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries)
        {
            return new StorageTreeBulkWriteBatch(estimatedEntries, scope.LookupStorageTree(address), this, address);
        }

        public event EventHandler<IWorldStateScopeProvider.AccountChangeEvent>? OnAccountChanged;

        public void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash)
        {
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));
        }

        private static Counter _writeBatchDispose = Prometheus.Metrics.CreateCounter("flatcache_writebatch_dispose_time", "hit rate", "part");

        public void Dispose()
        {
            long sw = Stopwatch.GetTimestamp();
            while (_dirtyStorageTree.TryDequeue(out var entry))
            {
                (AddressAsKey key, Hash256 storageRoot) = entry;
                if (!_dirtyAccounts.TryGetValue(key, out var account))
                {
                    account = scope.Get(key) ?? ThrowNullAccount(key);
                }

                account = account.WithChangedStorageRoot(storageRoot);

                OnAccountChanged?.Invoke(this, new IWorldStateScopeProvider.AccountChangeEvent(key, account));
                _dirtyAccounts[key] = account;

                if (logger.IsTrace) Trace(key, storageRoot, account);
            }
            _writeBatchDispose.WithLabels("dirty_storage").Inc(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            long storageRootUpdate = 0;

            using (var stateSetter = scope._backingStateTree.BeginSet(_dirtyAccounts.Count))
            {
                long ssw = Stopwatch.GetTimestamp();
                foreach (var kv in _dirtyAccounts)
                {
                    Account? account = kv.Value;
                    stateSetter.Set(kv.Key, account);
                }
                _writeBatchDispose.WithLabels("set").Inc(Stopwatch.GetTimestamp() - ssw);
            }
            _writeBatchDispose.WithLabels("whole").Inc(Stopwatch.GetTimestamp() - sw);
            _writeBatchDispose.WithLabels("storage_root_update").Inc(storageRootUpdate);

            scope.ClearLoadedAccounts();


            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account)
                => logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");

            [DoesNotReturn, StackTraceHidden]
            static Account ThrowNullAccount(Address address)
                => throw new InvalidOperationException($"Account {address} is null when updating storage hash");
        }
    }

    private class StorageTreeHintWrapper(
        IWorldStateScopeProvider.IStorageTree baseStorageTree,
#pragma warning disable CS9113 // Parameter is unread.
        ChannelWriter<(Address, UInt256?)> hintChan,
        Address address
#pragma warning restore CS9113 // Parameter is unread.
    ) : IWorldStateScopeProvider.IStorageTree
    {

        public Hash256 RootHash => baseStorageTree.RootHash;

        public byte[]? Get(in UInt256 index)
        {
            return baseStorageTree.Get(in index);
        }

        public void HintGet(in UInt256 index, byte[]? value)
        {
            // hintChan.TryWrite((address, index));
            baseStorageTree.HintGet(in index, value);
        }

        public byte[]? Get(in ValueHash256 hash)
        {
            return baseStorageTree.Get(in hash);
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
