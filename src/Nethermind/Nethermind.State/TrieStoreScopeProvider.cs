// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Intrinsics.X86;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

/// <param name="codeDbIsPersistent">
/// Default <c>false</c> is the safe, fail-closed setting: the wrapping ICodeDb assumes
/// nothing about durability and never reports cached "already persisted" hints.
/// Callers MUST explicitly pass <c>true</c> when <paramref name="codeDb"/> is durable
/// production storage (i.e. backed directly by RocksDB or equivalent) so the persisted-code
/// hint cache used by <c>StateProvider.InsertCode</c> can short-circuit redundant writes
/// of popular contract bytecode (factory contracts etc.).
/// Leaving it <c>false</c> for transient overlays is mandatory — otherwise the hint cache
/// would remember writes that get discarded with the overlay, and a subsequent scope on
/// the same StateProvider would skip re-inserting the bytes, throwing
/// "Code 0x… is missing from the database" on the next read.
/// </param>
public sealed class TrieStoreScopeProvider(
    ITrieStore trieStore,
    IKeyValueStoreWithBatching codeDb,
    IStateHeaderProvider stateHeaderProvider,
    ILogManager logManager,
    bool codeDbIsPersistent = false) : IWorldStateScopeProvider
{
    private readonly ITrieStore _trieStore = trieStore;
    private readonly IStateHeaderProvider _stateHeaderProvider = stateHeaderProvider;
    private readonly ILogManager _logManager = logManager;
    private StateTree? _backingStateTree;
    private readonly KeyValueWithBatchingBackedCodeDb _codeDb = new(codeDb, codeDbIsPersistent);

    private StateTree CreateStateTree() => new(_trieStore.GetTrieStore(null), _logManager);

    public bool HasRoot(BlockHeader? baseBlock) => _trieStore.HasRoot(baseBlock?.StateRoot ?? Keccak.EmptyTreeHash);

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => this.HasRootForTarget(_stateHeaderProvider, targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        this.TryBeginScopeAtBase(_stateHeaderProvider, targetBlock, metrics, out scope);

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        IDisposable trieStoreCloser = _trieStore.BeginScope(baseBlock);
        try
        {
            if (!HasRoot(baseBlock))
            {
                trieStoreCloser.Dispose();
                scope = null;
                return false;
            }

            StateTree backingStateTree = _backingStateTree ??= CreateStateTree();
            backingStateTree.RootHash = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;
            scope = new TrieStoreWorldStateBackendScope(backingStateTree, this, _codeDb, trieStoreCloser, _logManager);
            return true;
        }
        catch
        {
            trieStoreCloser.Dispose();
            throw;
        }
    }

    private StorageTree CreateStorageTree(Address address, Hash256 storageRoot) => new(_trieStore.GetTrieStore(address), storageRoot, _logManager);

    private class TrieStoreWorldStateBackendScope(StateTree backingStateTree, TrieStoreScopeProvider scopeProvider, IWorldStateScopeProvider.ICodeDb codeDb, IDisposable trieStoreCloser, ILogManager logManager) : IWorldStateScopeProvider.IScope
    {
        // Tracked HintBal background task — StartWriteBatch / Dispose cancel and drain it.
        private CancellationTokenSource? _hintBalCts;
        private Task? _hintBalTask;

        public void Dispose()
        {
            CancelHintBal();
            _trieStoreCloser.Dispose();
            _backingStateTree.RootHash = Keccak.EmptyTreeHash;
            _storages.Clear();
        }

        private void CancelHintBal()
        {
            // HintBal never starts the warm-up task on a single processor, so both fields are null.
            // Returning early keeps the task awaiter out of the guest image.
            if (Core.Cpu.RuntimeInformation.IsSingleProcessor) return;

            _hintBalCts?.Cancel();
            try { _hintBalTask?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ILogger logger = _logManager.GetClassLogger<TrieStoreWorldStateBackendScope>();
                if (logger.IsError) logger.Error("HintBal background task faulted during cancel/drain", ex);
            }
            _hintBalCts?.Dispose();
            _hintBalCts = null;
            _hintBalTask = null;
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

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
        {
            CancelHintBal();

            // This trie-store path has no trie warmer, so HintBal only does work when a sink is given.
            if (sink is null) return Task.CompletedTask;

            int accountCount = bal.AccountChanges.Count;
            if (accountCount == 0) return Task.CompletedTask;

            // The one processor that will run the block would only contend with a warm-up task, so
            // feed the sink inline; the guest folds the threaded path away.
            if (Core.Cpu.RuntimeInformation.IsSingleProcessor)
            {
                ReadOnlySpan<ReadOnlyAccountChanges> accounts = bal.AccountChanges.AsSpan();
                for (int i = 0; i < accounts.Length; i++)
                {
                    WarmAccount(sink, in accounts[i]);
                }

                return Task.CompletedTask;
            }

            // Copy the span into a pooled array so the Parallel.For body can capture it.
            ArrayPoolList<ReadOnlyAccountChanges> accountChanges = new(bal.AccountChanges.AsSpan());

            _hintBalCts = new CancellationTokenSource();
            CancellationToken token = _hintBalCts.Token;

            return _hintBalTask = Task.Run(() =>
            {
                // PatriciaTree.Get mutates shared TrieNode children in place as it resolves them,
                // so each Parallel.For iteration must own its StateTree / StorageTree — slots per
                // account are read sequentially on the worker that owns it.
                ParallelOptions parallelOptions = new() { CancellationToken = token };
                try
                {
                    Parallel.For(0, accountCount, parallelOptions, (i) =>
                    {
                        if (token.IsCancellationRequested) return;
                        ReadOnlyAccountChanges ac = accountChanges[i];
                        WarmAccount(sink, in ac);
                    });
                }
                catch (OperationCanceledException) { }
                finally
                {
                    accountChanges.Dispose();
                }
            });
        }

        private void WarmAccount(IWorldStateScopeProvider.IAsyncBalReaderSink sink, in ReadOnlyAccountChanges ac)
        {
            Address address = ac.Address;

            // Swallow MissingTrieNodeException so a partially-synced trie can't blow up
            // the background warmup task; it'll fault the main read path normally instead.
            try
            {
                StateTree privateStateTree = _scopeProvider.CreateStateTree();
                privateStateTree.RootHash = _backingStateTree.RootHash;

                Account? account;
                if (sink.StillNeeded(address, out Account? cached))
                {
                    account = privateStateTree.Get(address);
                    sink.OnAccountRead(address, account);
                }
                else
                {
                    account = cached;
                }

                if (account is null) return;
                Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
                if (storageRoot == Keccak.EmptyTreeHash) return;

                ReadOnlySpan<UInt256> changed = ac.ChangedSlots;
                ReadOnlySpan<UInt256> reads = ac.StorageReads;
                if (changed.Length + reads.Length == 0) return;

                // Sorted-merge walk over (ChangedSlots, StorageReads) — both arrays are
                // ascending and disjoint, so one merged pass keeps adjacent trie paths
                // hot across consecutive Get calls.
                StorageTree storageTree = _scopeProvider.CreateStorageTree(address, storageRoot);
                int slotIndex = 0;
                int readIndex = 0;
                while (slotIndex < changed.Length || readIndex < reads.Length)
                {
                    UInt256 slot;
                    if (readIndex >= reads.Length)
                    {
                        slot = changed[slotIndex++];
                    }
                    else
                    {
                        slot = reads[readIndex];
                        if (slotIndex < changed.Length && changed[slotIndex].CompareTo(in slot) <= 0)
                        {
                            slot = changed[slotIndex++];
                        }
                        else
                        {
                            readIndex++;
                        }
                    }
                    StorageCell cell = new(address, in slot);
                    if (!sink.StillNeeded(in cell)) continue;
                    storageTree.Get(in slot, out UInt256 value);
                    sink.OnStorageRead(in cell, in value);
                }
            }
            catch (MissingTrieNodeException) { }
        }

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb1;

        internal StateTree _backingStateTree = backingStateTree;
        private readonly Dictionary<AddressAsKey, StorageTree> _storages = [];
        private readonly Dictionary<AddressAsKey, Account?> _loadedAccounts = [];
        private readonly TrieStoreScopeProvider _scopeProvider = scopeProvider;
        private readonly IWorldStateScopeProvider.ICodeDb _codeDb1 = codeDb;
        private readonly IDisposable _trieStoreCloser = trieStoreCloser;
        private readonly ILogManager _logManager = logManager;

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNumber)
        {
            CancelHintBal();
            return new WorldStateWriteBatch(this, estimatedAccountNumber, _logManager.GetClassLogger<TrieStoreWorldStateBackendScope>());
        }

        public void Commit(ulong blockNumber)
        {
            foreach (KeyValuePair<AddressAsKey, StorageTree> storage in _storages)
            {
                storage.Value.Commit();
            }

            _backingStateTree.Commit();
            _storages.Clear();
        }

        internal StorageTree LookupStorageTree(Address address)
        {
            if (_storages.TryGetValue(address, out StorageTree? storageTree))
            {
                return storageTree;
            }

            storageTree = _scopeProvider.CreateStorageTree(address, Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash);
            _storages[address] = storageTree;
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
        private Action<Address, Hash256>? _markDirty;

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account) => _dirtyAccounts[key] = account;

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries) =>
            new StorageTreeBulkWriteBatch(estimatedEntries, scope.LookupStorageTree(address), _markDirty ??= MarkDirty, address);

        private void MarkDirty(Address address, Hash256 storageTreeRootHash) => _dirtyStorageTree.Enqueue((address, storageTreeRootHash));

        public void Dispose()
        {
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
                Address address = key.Value;
                OnAccountUpdated?.Invoke(address, new IWorldStateScopeProvider.AccountUpdated(address, account));
                if (logger.IsTrace) Trace(address, storageRoot, account);
            }

            OnAccountUpdated = null;

            if (Avx2.IsSupported && _dirtyAccounts.Count >= KeyHashBatch.MinimumBatchSize)
            {
                scope._backingStateTree.SetAccounts(_dirtyAccounts);
            }
            else
            {
                using StateTree.StateTreeBulkSetter stateSetter = scope._backingStateTree.BeginSet(_dirtyAccounts.Count);
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
}
