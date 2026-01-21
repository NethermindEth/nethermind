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
    private TwoStringLabel _storageTreeCreateTimeLabel = null!;
    private TwoStringLabel _storageTreeCreateGetTimeLabel = null!;
    private TwoStringLabel _statePrewarmPausedLabel = null!;
    private TwoStringLabel _statePrewarmWrongNumLabel = null!;
    private TwoStringLabel _stateTreePrewarmLabel = null!;

    private readonly SnapshotBundle _snapshotBundle;
    private readonly IWorldStateScopeProvider.ICodeDb _codeDb;
    private readonly IFlatDbManager _flatDbManager;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = new();
    private readonly StateTree _stateTree;
    private readonly PatriciaTree _warmupStateTree;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly IFlatDbConfig _configuration;
    private readonly ConcurrencyController _concurrencyQuota;
    private readonly bool _disableLocalAddressTriewarmerQueue;
    internal readonly bool _disableLocalSlotTriewarmerQueue;
    private readonly ITrieWarmer _warmer;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmers's ring buffer.
    private int _hintSequenceId = 0;
    private StateId _currentStateId;
    internal bool _pausePrewarmer = false;
    private readonly string _isPrewarmerLabel;

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatDbManager flatDbManager,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        _codeDb = codeDb;
        _flatDbManager = flatDbManager;
        _isPrewarmerLabel = (_warmer is NoopTrieWarmer).ToString();

        _concurrencyQuota = new ConcurrencyController(Environment.ProcessorCount); // Used during tree commit.
        _stateTree = new StateTree(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota, false),
            logManager
        );
        _stateTree.RootHash = currentStateId.StateRoot.ToCommitment();
        _warmupStateTree = new PatriciaTree(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota, true),
            logManager
        );
        _warmupStateTree.RootHash = currentStateId.StateRoot.ToCommitment();

        _configuration = configuration;
        _logManager = logManager;
        _warmer = trieCacheWarmer;
        _storageTreeCreateTimeLabel = new TwoStringLabel("storage_create", _isPrewarmerLabel);
        _storageTreeCreateGetTimeLabel = new TwoStringLabel("storage_create_get_time", _isPrewarmerLabel);
        _statePrewarmPausedLabel = new TwoStringLabel("state_prewarm_paused", _isPrewarmerLabel);
        _statePrewarmWrongNumLabel = new TwoStringLabel("state_prewarm_wrong_num", _isPrewarmerLabel);

        _stateTreePrewarmLabel = new TwoStringLabel("state_tree_prewarm", _isPrewarmerLabel);

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
        _disableLocalAddressTriewarmerQueue = false;
        _disableLocalSlotTriewarmerQueue = false;
    }

    public void Dispose()
    {
        _snapshotBundle.Dispose();
        _warmer.OnExitScope();
    }

    public void ResetState()
    {
        // Clear storage tree cache
        lock (_storages)
        {
            _storages.Clear();
        }

        // Reset the snapshot bundle
        _snapshotBundle.Reset();

        // Reset state trees to original root
        _stateTree.RootHash = _currentStateId.StateRoot.ToCommitment();
        _warmupStateTree.RootHash = _currentStateId.StateRoot.ToCommitment();

        // Invalidate in-flight trie warmer tasks
        Interlocked.Increment(ref _hintSequenceId);
    }

    public Hash256 RootHash => _stateTree.RootHash;
    public void UpdateRootHash() => _stateTree.UpdateRootHash();

    public Account? Get(Address address)
    {
        Account? account;
        if (_configuration.ReadWithTrie)
        {
            account = _stateTree.Get(address);
        }
        else
        {
            if (!_snapshotBundle.TryGetAccount(address, out account))
            {
            }
        }

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
        if (!_disableLocalAddressTriewarmerQueue)
        {
            if (_snapshotBundle.ShouldQueuePrewarm(address, null)) _warmer.PushAddressJob(this, address, _hintSequenceId, false);
        }
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;
    public int HintSequenceId => _hintSequenceId; // Called by FlatStorageTree

    public bool WarmUpStateTrie(Address address, int sequenceId, bool isWrite)
    {
        if (_hintSequenceId != sequenceId)
        {
            return false;
        }
        if (_pausePrewarmer)
        {
            return false;
        }

        long sw = Stopwatch.GetTimestamp();
        try
        {
            // Note: tree root not changed after write batch. Also not cleared. So the result is not correct.
            // this is just for warming up
            _warmupStateTree.WarmUpPath(address.ToAccountPath.Bytes, isWrite);
        }
        catch (AbstractMinimalTrieStore.UnsupportedOperationException)
        {
            // So there is this highly confusing case where patriciatree attempted to set storage nodes as persisted
            // if its parent is persisted. No idea what is the case, but in this case, we really dont care.
        }
        finally
        {
            Metrics.FlatScopeTime.Observe(Stopwatch.GetTimestamp() - sw, _stateTreePrewarmLabel);
        }

        return true;
    }

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => CreateStorageTreeImpl(address);

    private FlatStorageTree CreateStorageTreeImpl(Address address)
    {
        lock (_storages)
        {
            ref FlatStorageTree? storage = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
            if (exists)
            {
                return storage!;
            }

            long sw = Stopwatch.GetTimestamp();
            Hash256 storageRoot = Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
            Metrics.FlatScopeTime.Observe(Stopwatch.GetTimestamp() - sw, _storageTreeCreateGetTimeLabel);
            sw = Stopwatch.GetTimestamp();
            storage = new FlatStorageTree(
                this,
                _warmer,
                _snapshotBundle,
                _configuration,
                _concurrencyQuota,
                storageRoot,
                address,
                _logManager);
            Metrics.FlatScopeTime.Observe(Stopwatch.GetTimestamp() - sw, _storageTreeCreateTimeLabel);

            return storage;
        }
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        // Invalidates trie node warmer tasks at this point. Write batch already do things in parallel.
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(long blockNumber)
    {
        _pausePrewarmer = true;
        StateId newStateId = new StateId(blockNumber, RootHash);
        if (!_isReadOnly)
        {
            long sw = Stopwatch.GetTimestamp();
            // Commit will copy the trie nodes from the tree to the bundle.
            using ArrayPoolList<Task> commitTask = new ArrayPoolList<Task>(_storages.Count);

            commitTask.Add(Task.Factory.StartNew(() =>
            {
                sw = Stopwatch.GetTimestamp();
                // Commit will copy the trie nodes from the tree to the bundle.
                _stateTree.Commit();
                Metrics.FlatScopeTime.Observe(Stopwatch.GetTimestamp() - sw, new TwoStringLabel("statetree_commit", _isPrewarmerLabel));
            }));

            lock (_storages)
            {
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
            }

            Task.WaitAll(commitTask.AsSpan());
            Metrics.FlatScopeTime.Observe(Stopwatch.GetTimestamp() - sw, new TwoStringLabel("storage_commit_wait", _isPrewarmerLabel));
        }

        lock (_storages)
        {
            _storages.Clear();
        }

        bool shouldAddSnapshot = !_isReadOnly && _currentStateId != newStateId;

        (Snapshot? newSnapshot, TransientResource? cachedResource) = _snapshotBundle.CollectAndApplySnapshot(_currentStateId, newStateId, shouldAddSnapshot);

        if (shouldAddSnapshot)
        {
            if (_currentStateId != newStateId)
            {
                _flatDbManager.AddSnapshot(newSnapshot!, cachedResource!);
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

    private class WriteBatch(
        FlatWorldStateScope scope,
        int estimatedAccountCount,
        ILogger logger
    ) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly ConcurrentDictionary<AddressAsKey, Account?> _dirtyAccounts = new(Environment.ProcessorCount, estimatedAccountCount);
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
            try
            {
                long sw = Stopwatch.GetTimestamp();
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
                Metrics.FlatScopeTime.Observe(Stopwatch.GetTimestamp() - sw, new TwoStringLabel("dirtystorage_dequeue", scope._isPrewarmerLabel));

                using (var stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count))
                {
                    sw = Stopwatch.GetTimestamp();
                    foreach (var kv in _dirtyAccounts)
                    {
                        stateSetter.Set(kv.Key, kv.Value);
                    }
                    Metrics.FlatScopeTime.Observe(Stopwatch.GetTimestamp() - sw, new TwoStringLabel("account_set", scope._isPrewarmerLabel));
                    sw = Stopwatch.GetTimestamp();
                }
                Metrics.FlatScopeTime.Observe(Stopwatch.GetTimestamp() - sw, new TwoStringLabel("account_set_dispose", scope._isPrewarmerLabel));
            }
            finally
            {
                _dirtyAccounts.Clear();

                Interlocked.Increment(ref scope._hintSequenceId);
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
