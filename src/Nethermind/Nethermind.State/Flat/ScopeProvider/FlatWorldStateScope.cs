// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatWorldStateScope : IWorldStateScopeProvider.IScope
{
    // internal static Address DebugAddress = new Address("0x2c2b2df915e31d27e7a24c7c3cf9b114208a45e0");
    internal static Address DebugAddress = new Address("0x6ffedc1562918c07ae49b0ba210e6d80c7d61eab");
    internal static UInt256 DebugSlot = UInt256.Parse("0");

    private readonly SnapshotBundle _snapshotBundle;
    private readonly IWorldStateScopeProvider.ICodeDb _codeDb;
    private readonly IFlatDiffRepository _flatDiffRepository;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = new();
    private readonly StateTree _stateTree;
    private readonly PatriciaTree _warmupStateTree;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly FlatDiffRepository.Configuration _configuration;
    private readonly ConcurrencyQuota _concurrencyQuota;
    private readonly ITrieWarmer _warmer;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmers's ring buffer.
    private int _hintSequenceId = 0;
    private StateId _currentStateId;

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatDiffRepository flatDiffRepository,
        FlatDiffRepository.Configuration configuration,
        ITrieWarmer trieCacheWarmer,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        _codeDb = codeDb;
        _flatDiffRepository = flatDiffRepository;

        _concurrencyQuota = new ConcurrencyQuota(); // Used during tree commit.
        _stateTree = new StateTree(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota, isTrieWarmer: false),
            logManager
        );
        _stateTree.RootHash = currentStateId.stateRoot.ToCommitment();
        _warmupStateTree = new PatriciaTree(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota, isTrieWarmer: true),
            logManager
        );
        _warmupStateTree.RootHash = currentStateId.stateRoot.ToCommitment();

        _configuration = configuration;
        _logManager = logManager;
        _warmer = trieCacheWarmer;
        _warmer.OnNewScope();
        _isReadOnly = isReadOnly;
    }

    public void Dispose() => _snapshotBundle.Dispose();
    public Hash256 RootHash => _stateTree.RootHash;
    public void UpdateRootHash() => _stateTree.UpdateRootHash();

    public Account? Get(Address address)
    {
        if (!_configuration.ReadWithTrie && _snapshotBundle.TryGetAccount(address, out Account account))
        {
            HintGet(address, account);

            if (address == DebugAddress)
            {
                Account? accTrie = _stateTree.Get(address);
                Console.Error.WriteLine($"Address Get {account}. Tree {accTrie}");
            }

            if (_configuration.VerifyWithTrie)
            {
                Account? accTrie = _stateTree.Get(address);
                if (accTrie != account)
                {
                    throw new Exception($"Incorrect account {address}, account hash {address.ToAccountPath}, trie: {accTrie} vs flat: {account}");
                }
            }

            return account;
        }
        else
        {
            account = _stateTree.Get(address);
            HintGet(address, account);
            return account;
        }
    }

    public void HintGet(Address address, Account? account)
    {
        _warmer.PushJob(this, address, null, null, _hintSequenceId);

        // during storage root update, the account will get re-fetched then updated.
        _snapshotBundle.SetAccount(address, account);
    }

    public void HintSet(Address address)
    {
        _warmer.PushJob(this, address, null, null, _hintSequenceId);
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;
    public int HintSequenceId => _hintSequenceId; // Called by FlatStorageTree

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        if (_hintSequenceId != sequenceId) return false;

        try
        {
            if (_snapshotBundle.ShouldPrewarm(address, null))
            {
                // Note: tree root not changed after write batch. Also not cleared. So the result is not correct.
                // this is just for warming up
                _ = _warmupStateTree.Get(address.ToAccountPath.Bytes);
            }
        }
        catch (AbstractMinimalTrieStore.UnsupportedOperationException)
        {
            // So there is this highly confusing case where patriciatree attempted to set storage nodes as persisted
            // if its parent is persisted. No idea what is the case, but in this case, we really dont care.
        }

        return true;
    }

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => CreateStorageTreeImpl(address);

    private FlatStorageTree CreateStorageTreeImpl(Address address)
    {
        ref FlatStorageTree storage = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (exists)
        {
            return storage;
        }

        Hash256 storageRoot = Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        storage = new FlatStorageTree(
            this,
            _warmer,
            _snapshotBundle,
            _configuration,
            _concurrencyQuota,
            storageRoot,
            address,
            _logManager);
        if (address == DebugAddress)
        {
            var val = storage.Get(DebugSlot);
            Console.Error.WriteLine($"Debug value is {val?.ToHexString()}");
        }

        return storage;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        // Invalidates trie node warmer tasks at this point. Write batch already do things in parallel.
        Interlocked.Increment(ref _hintSequenceId);

        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(long blockNumber)
    {
        StateId newStateId = new StateId(blockNumber, RootHash);
        if (!_isReadOnly)
        {

            // Commit will copy the trie nodes from the tree to the bundle.
            using ArrayPoolList<Task> commitTask = new ArrayPoolList<Task>(_storages.Count);

            foreach (KeyValuePair<AddressAsKey, FlatStorageTree> storage in _storages)
            {
                if (_concurrencyQuota.TryRequestConcurrencyQuota())
                {
                    commitTask.Add(Task.Factory.StartNew((ctx) =>
                    {
                        FlatStorageTree st = (FlatStorageTree)ctx;
                        st.CommitTree();
                        _concurrencyQuota.ReturnConcurrencyQuota();
                    }, storage.Value));
                }
                else
                {
                    storage.Value.CommitTree();
                }
            }

            Task.WaitAll(commitTask.AsSpan());

            _stateTree.Commit();
        }

        _storages.Clear();

        (Snapshot newSnapshot, CachedResource cachedResource) = _snapshotBundle.CollectAndApplySnapshot(_currentStateId, newStateId, _isReadOnly);

        if (!_isReadOnly)
        {
            if (_currentStateId != newStateId) _flatDiffRepository.AddSnapshot(newSnapshot, cachedResource);
        }

        _currentStateId = newStateId;
    }

    private class WriteBatch(
        FlatWorldStateScope scope,
        int estimatedAccountCount,
        ILogger logger
    ) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account)
        {
            if (key == DebugAddress) Console.Error.WriteLine($"Address Set {account}");
            _dirtyAccounts[key] = account;
            scope._snapshotBundle.SetAccount(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries)
        {
            return scope
                .CreateStorageTreeImpl(address)
                .CreateWriteBatch(
                    estimatedEntries: estimatedEntries,
                    onRootUpdated: (address, newRoot) => MarkDirty(address, newRoot));
        }

        private void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash)
        {
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));
        }

        public void Dispose()
        {
            bool hasIt = false;
            try
            {
                while (_dirtyStorageTree.TryDequeue(out var entry))
                {
                    (AddressAsKey key, Hash256 storageRoot) = entry;
                    if (!_dirtyAccounts.TryGetValue(key, out var account)) account = scope.Get(key);
                    if (account == null && storageRoot == Keccak.EmptyTreeHash) continue;
                    account ??= ThrowNullAccount(key);
                    account = account!.WithChangedStorageRoot(storageRoot);
                    // if (key == DebugAddress) Console.Error.WriteLine($"Address root update {account}");
                    scope._snapshotBundle.SetAccount(key, account);
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
            }
            finally
            {
                _dirtyAccounts.Clear();

                if (hasIt)
                {
                    Console.Error.WriteLine($"Exit. Seuence it is {scope._hintSequenceId}");
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account)
                => logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");

            [DoesNotReturn, StackTraceHidden]
            static Account ThrowNullAccount(Address address)
                => throw new InvalidOperationException($"Account {address} is null when updating storage hash");
        }
    }
}
