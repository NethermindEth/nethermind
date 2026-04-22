// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;

    private readonly ConcurrencyController _concurrencyQuota;
    private readonly PatriciaTree _warmupStateTree;
    private readonly StateTree _stateTree;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = new();
    private readonly Lock _storagesLock = new();
    private bool _isDisposed = false;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmer's ring buffer.
    private int _hintSequenceId = 0;
    private StateId _currentStateId;
    internal bool _pausePrewarmer = false;

    // Tracked HintBal background task — mirrors PrewarmerScopeProvider's _balCts/_balTask pattern.
    private CancellationTokenSource? _hintBalCts;
    private Task? _hintBalTask;

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
        CodeDb = codeDb;
        _commitTarget = commitTarget;

        _concurrencyQuota = new ConcurrencyController(Environment.ProcessorCount); // Used during tree commit.
        _stateTree = new(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota),
            logManager
        )
        {
            RootHash = currentStateId.StateRoot.ToCommitment()
        };

        _warmupStateTree = new(
            new StateTrieStoreWarmerAdapter(snapshotBundle),
            logManager
        )
        {
            RootHash = currentStateId.StateRoot.ToCommitment()
        };

        _configuration = configuration;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        CancelHintBal();
        _snapshotBundle.Dispose();
        _warmer.OnExitScope();
    }

    private void CancelHintBal()
    {
        _hintBalCts?.Cancel();
        try { _hintBalTask?.GetAwaiter().GetResult(); }
        catch { }
        _hintBalCts?.Dispose();
        _hintBalCts = null;
        _hintBalTask = null;
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
                throw new TrieException($"Incorrect account {address}, account hash {address.ToAccountPath}, trie: {accTrie} vs flat: {account}");
            }
        }

        return account;
    }

    public void HintGet(Address address, Account? account)
    {
        _snapshotBundle.SetAccount(address, account);
        if (_snapshotBundle.ShouldQueuePrewarm(address))
        {
            _warmer.PushAddressJob(this, address, _hintSequenceId);
        }
    }

    public void HintBal(BlockAccessList bal)
    {
        // Cancel any previous HintBal task and start a new one. Tracks the task so
        // Dispose / next HintBal can stop it cleanly, matching PrewarmerScopeProvider's pattern.
        CancelHintBal();
        _hintBalCts = new CancellationTokenSource();
        CancellationToken token = _hintBalCts.Token;
        int snapshot = _hintSequenceId;

        _hintBalTask = Task.Run(() =>
        {
            try
            {
                foreach (AccountChanges accountChanges in bal.AccountChanges)
                {
                    if (token.IsCancellationRequested) return;
                    if (_hintSequenceId != snapshot || _pausePrewarmer) return;

                    Address address = accountChanges.Address;
                    _warmer.PushAddressJob(this, address, snapshot);

                    int slotCount = accountChanges.StorageChanges.Count + accountChanges.StorageReads.Count;
                    if (slotCount == 0) continue;

                    Account? account = _snapshotBundle.GetAccount(address);
                    Hash256 storageRoot = account?.StorageRoot ?? Keccak.EmptyTreeHash;
                    if (storageRoot == Keccak.EmptyTreeHash) continue;

                    // Non-cached per-warmup tree — the main thread's CreateStorageTreeImpl still
                    // builds its own cached instance the first time it's asked.
                    FlatStorageTree storageWarmer = new(
                        this,
                        _warmer,
                        _snapshotBundle,
                        _configuration,
                        _concurrencyQuota,
                        storageRoot,
                        address,
                        _logManager);

                    foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
                    {
                        if (token.IsCancellationRequested) return;
                        _warmer.PushSlotJobMpmc(storageWarmer, slotChanges.Slot, snapshot);
                    }

                    foreach (StorageRead storageRead in accountChanges.StorageReads)
                    {
                        if (token.IsCancellationRequested) return;
                        _warmer.PushSlotJobMpmc(storageWarmer, storageRead.Key, snapshot);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }, token);
    }

    public Task ReadBalAsync(BlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink sink, CancellationToken cancellationToken)
    {
        int accountCount = 0;
        foreach (AccountChanges _ in bal.AccountChanges)
            accountCount++;

        if (accountCount == 0)
            return Task.CompletedTask;

        using ArrayPoolList<AccountChanges> accountChangesList = new(accountCount, accountCount);
        int copyIdx = 0;
        foreach (AccountChanges ac in bal.AccountChanges)
            accountChangesList[copyIdx++] = ac;

        ParallelOptions parallelOptions = new() { CancellationToken = cancellationToken };

        // Phase 1: parallel account fetch, gated by the sink
        using ArrayPoolList<Account?> accounts = new(accountCount, accountCount);
        try
        {
            Parallel.For(0, accountCount, parallelOptions, (i) =>
            {
                Address address = accountChangesList[i].Address;
                if (!sink.StillNeeded(address, out Account? cached))
                {
                    accounts[i] = cached;
                    return;
                }

                Account? account = _snapshotBundle.GetAccount(address);
                accounts[i] = account;
                sink.OnAccountRead(address, account);
            });
        }
        catch (OperationCanceledException) { return Task.CompletedTask; }

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 2: flatten (address, tree, slot) jobs and read in one parallel pass
        int totalSlots = 0;
        for (int i = 0; i < accountCount; i++)
        {
            Account? account = accounts[i];
            if (account is null) continue;
            Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
            if (storageRoot == Keccak.EmptyTreeHash) continue;
            totalSlots += accountChangesList[i].StorageChanges.Count + accountChangesList[i].StorageReads.Count;
        }

        if (totalSlots > 0)
        {
            using ArrayPoolList<(Address Address, IWorldStateScopeProvider.IStorageTree Tree, UInt256 Slot)> jobs = new(totalSlots, totalSlots);
            int jobIdx = 0;
            for (int i = 0; i < accountCount; i++)
            {
                Account? account = accounts[i];
                if (account is null) continue;
                Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
                if (storageRoot == Keccak.EmptyTreeHash) continue;

                AccountChanges accountChanges = accountChangesList[i];
                Address address = accountChanges.Address;
                IWorldStateScopeProvider.IStorageTree storageTree = CreateStorageTree(address);

                foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
                    jobs[jobIdx++] = (address, storageTree, slotChanges.Slot);

                foreach (StorageRead storageRead in accountChanges.StorageReads)
                    jobs[jobIdx++] = (address, storageTree, storageRead.Key);
            }

            try
            {
                Parallel.For(0, totalSlots, parallelOptions, (s) =>
                {
                    (Address address, IWorldStateScopeProvider.IStorageTree tree, UInt256 slot) = jobs[s];
                    StorageCell cell = new(address, in slot);
                    if (!sink.StillNeeded(in cell))
                        return;

                    byte[] value = tree.Get(in slot);
                    sink.OnStorageRead(in cell, value);
                });
            }
            catch (OperationCanceledException) { }
        }

        return Task.CompletedTask;
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public int HintSequenceId => _hintSequenceId; // Called by FlatStorageTree

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        if (_hintSequenceId != sequenceId || _pausePrewarmer) return false;

        // Note: tree root not changed after writing batch. Also, not cleared. So the result is not correct.
        // this is just for warming up
        _warmupStateTree.WarmUpPath(address.ToAccountPath.Bytes);

        return true;
    }

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => CreateStorageTreeImpl(address);

    private FlatStorageTree CreateStorageTreeImpl(Address address)
    {
        // _storages is accessed concurrently — the HintBal/ReadBalAsync Task.Run may call
        // CreateStorageTree from a worker thread while the main processing thread also
        // reads/writes via scope.Get/WriteBatch paths. Dictionary is not thread-safe.
        lock (_storagesLock)
        {
            ref FlatStorageTree? storage = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
            if (exists) return storage!;

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

            return storage;
        }
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
        new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());

    public void Commit(long blockNumber)
    {
        _pausePrewarmer = true;
        // Drain HintBal task before iterating _storages — Commit's foreach isn't locked
        // and the task may otherwise be mid-CreateStorageTreeImpl when we start iterating.
        CancelHintBal();

        using ArrayPoolListRef<Task> commitTask = new(_storages.Count);

        commitTask.Add(Task.Factory.StartNew(() =>
        {
            // Commit will copy the trie nodes from the tree to the bundle.
            // Its fine to commit the state tree together with the storage tree at this point as the storage tree
            // root has been resolved and updated to the state tree within the writebatch.
            _stateTree.Commit();
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));

        foreach (KeyValuePair<AddressAsKey, FlatStorageTree> storage in _storages)
        {
            if (_concurrencyQuota.TryRequestConcurrencyQuota())
            {
                commitTask.Add(Task.Factory.StartNew((ctx) =>
                {
                    FlatStorageTree st = (FlatStorageTree)ctx!;
                    st.CommitTree();
                    _concurrencyQuota.ReturnConcurrencyQuota();
                }, storage.Value, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default));
            }
            else
            {
                storage.Value.CommitTree();
            }
        }

        Task.WaitAll(commitTask.AsSpan());

        _storages.Clear();

        StateId newStateId = new(blockNumber, RootHash);
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

            if (account is null)
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
                    if (account is null)
                    {
                        if (storageRoot == Keccak.EmptyTreeHash) continue;
                        using IWorldStateScopeProvider.IStorageWriteBatch wb = CreateStorageWriteBatch(entry.Item1, 0);
                        wb.Clear();
                        continue;
                    }
                    account = account.WithChangedStorageRoot(storageRoot);
                    _dirtyAccounts[key] = account;

                    scope._snapshotBundle.SetAccount(key, account);

                    OnAccountUpdated?.Invoke(key, new IWorldStateScopeProvider.AccountUpdated(key, account));
                    if (logger.IsTrace) Trace(key, storageRoot, account);
                }

                using StateTree.StateTreeBulkSetter stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count);
                foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                {
                    stateSetter.Set(kv.Key, kv.Value);
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
        }
    }
}
