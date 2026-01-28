// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatWorldStateScope : IWorldStateScopeProvider.IScope, ITrieWarmer.IAddressWarmer
{
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IWorldStateScopeProvider.ICodeDb _codeDb;
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;

    private readonly ConcurrencyController _concurrencyQuota;
    private readonly PatriciaTree _warmupStateTree;
    private readonly StateTree _stateTree;
    private readonly ConcurrentDictionary<AddressAsKey, FlatStorageTree> _storages = new();
    private bool _isDisposed = false;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmers's ring buffer.
    private int _hintSequenceId = 0;
    private StateId _currentStateId;
    internal bool _pausePrewarmer = false;

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatCommitTarget commitTarget,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        _codeDb = codeDb;
        _commitTarget = commitTarget;

        _concurrencyQuota = new ConcurrencyController(Environment.ProcessorCount); // Used during tree commit.
        _stateTree = new StateTree(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota),
            logManager
        );
        _stateTree.RootHash = currentStateId.StateRoot.ToCommitment();
        _warmupStateTree = new PatriciaTree(
            new StateTrieStoreWarmerAdapter(snapshotBundle),
            logManager
        );
        _warmupStateTree.RootHash = currentStateId.StateRoot.ToCommitment();

        _configuration = configuration;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        _snapshotBundle.Dispose();
        _warmer.OnExitScope();
    }

    public Hash256 RootHash => _stateTree.RootHash;
    public void UpdateRootHash() => _stateTree.UpdateRootHash();

    public Account? Get(Address address)
    {
        Account? account = _snapshotBundle.GetAccount(address);

        HintGet(address, account);

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

    public void HintGet(Address address, Account? account)
    {
        _snapshotBundle.SetAccount(address, account);
        if (_snapshotBundle.ShouldQueuePrewarm(address, null))
            _warmer.PushAddressJob(this, address, _hintSequenceId, false);
    }

    public void HintSet(Address address)
    {
        if (_configuration.DisableHintSetWarmup) return;
        if (_snapshotBundle.ShouldQueuePrewarm(address, null))
            _warmer.PushAddressJob(this, address, _hintSequenceId, true);
    }

    public void WarmUpOutOfScope(Address address, UInt256? slot, bool isWrite)
    {
        if (_isDisposed) return;
        if (_pausePrewarmer) return;
        if (isWrite && _configuration.DisableHintSetWarmup) return;
        if (_configuration.DisableOutOfScopeWarmup) return;

        if (slot is null)
        {
            if (_snapshotBundle.ShouldQueuePrewarm(address, null)) _warmer.PushJobMulti(this, address, null, null, _hintSequenceId, isWrite);
        }
        else
        {
            CreateStorageTreeImpl(address).QueueOutOfScopeWarmup(slot.Value, isWrite);
        }
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;
    public int HintSequenceId => _hintSequenceId; // Called by FlatStorageTree

    public bool WarmUpStateTrie(Address address, int sequenceId, bool isWrite)
    {
        if (_hintSequenceId != sequenceId || _pausePrewarmer) return false;

        // Note: tree root not changed after write batch. Also not cleared. So the result is not correct.
        // this is just for warming up
        _warmupStateTree.WarmUpPath(address.ToAccountPath.Bytes);

        return true;
    }

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => CreateStorageTreeImpl(address);

    private FlatStorageTree CreateStorageTreeImpl(Address address)
    {
        if (_storages.TryGetValue(address, out FlatStorageTree? tree)) return tree;

        // Get the storage root ahead of time out of lock
        Hash256 storageRoot = Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;

        lock (_storages)
        {
            // Double check
            if (_storages.TryGetValue(address, out tree)) return tree;

            tree = new FlatStorageTree(
                this,
                _warmer,
                _snapshotBundle,
                _configuration,
                _concurrencyQuota,
                storageRoot,
                address,
                _logManager);

            _storages[address] = tree;
            return tree;
        }
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
        new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());

    public void Commit(long blockNumber)
    {
        _pausePrewarmer = true;

        using ArrayPoolListRef<Task> commitTask = new ArrayPoolListRef<Task>(_storages.Count);

        commitTask.Add(Task.Factory.StartNew(() =>
        {
            // Commit will copy the trie nodes from the tree to the bundle.
            // Its fine to commit the state tree together with the storage tree at this point as the storage tree
            // root has been resolve and updated to state tree within the writebatch.
            _stateTree.Commit();
        }));

        foreach (KeyValuePair<AddressAsKey, FlatStorageTree> storage in _storages)
        {
            if (_concurrencyQuota.TryRequestConcurrencyQuota())
            {
                commitTask.Add(Task.Factory.StartNew((ctx) =>
                {
                    FlatStorageTree st = (FlatStorageTree)ctx!;
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

        _storages.Clear();

        StateId newStateId = new StateId(blockNumber, RootHash);
        bool shouldAddSnapshot = !_isReadOnly && _currentStateId != newStateId;
        (Snapshot? newSnapshot, TransientResource? cachedResource) = _snapshotBundle.CollectAndApplySnapshot(_currentStateId, newStateId, shouldAddSnapshot);

        if (shouldAddSnapshot)
        {
            if (_currentStateId != newStateId)
            {
                _commitTarget.AddSnapshot(newSnapshot!, cachedResource!);
            }
            else
            {
                newSnapshot?.Dispose();
                cachedResource?.Dispose();
            }
        }

        _currentStateId = newStateId;
        _pausePrewarmer = false;
    }

    // Largely same logic as the the one for TrieStoreScopeProvider, but more confusing when deduplicated.
    // So I just leave it here.
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
            _dirtyAccounts[key] = account;
            scope._snapshotBundle.SetAccount(key, account);

            if (account == null)
            {
                // This may not get called by the storage write batch as the worldstate does not try to update storage
                // at all if the end account is null. This is not a problem for trie, but is a problem for flat.
                scope.CreateStorageTreeImpl(key).SelfDestruct();
            }
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries) =>
            scope
                .CreateStorageTreeImpl(address)
                .CreateWriteBatch(
                    estimatedEntries: estimatedEntries,
                    onRootUpdated: (address, newRoot) => MarkDirty(address, newRoot));

        private void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash) =>
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));

        public void Dispose()
        {
            try
            {
                while (_dirtyStorageTree.TryDequeue(out (AddressAsKey, Hash256) entry))
                {
                    (AddressAsKey key, Hash256 storageRoot) = entry;
                    if (!_dirtyAccounts.TryGetValue(key, out Account? account)) account = scope.Get(key);
                    if (account == null && storageRoot == Keccak.EmptyTreeHash) continue;
                    account ??= ThrowNullAccount(key);
                    account = account!.WithChangedStorageRoot(storageRoot);
                    _dirtyAccounts[key] = account;

                    scope._snapshotBundle.SetAccount(key, account);

                    OnAccountUpdated?.Invoke(key, new IWorldStateScopeProvider.AccountUpdated(key, account));
                    if (logger.IsTrace) Trace(key, storageRoot, account);
                }

                using (StateTree.StateTreeBulkSetter stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count))
                {
                    foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                    {
                        stateSetter.Set(kv.Key, kv.Value);
                    }
                }
            }
            finally
            {
                _dirtyAccounts.Clear();

                Interlocked.Increment(ref scope._hintSequenceId);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account) =>
                logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");

            [DoesNotReturn, StackTraceHidden]
            static Account ThrowNullAccount(Address address) =>
                throw new InvalidOperationException($"Account {address} is null when updating storage hash");
        }
    }
}
