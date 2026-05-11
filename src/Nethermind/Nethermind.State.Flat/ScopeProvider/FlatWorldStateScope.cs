// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
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
    private bool _isDisposed = false;

    // tasks within the trie warmer's ring buffer. Volatile so the background HintBal task sees increments without
    // conflicting with the Interlocked writer.
    private volatile int _hintSequenceId = 0;
    private int _outstandingWarmups = 0;
    private StateId _currentStateId;
    internal volatile bool _pausePrewarmer = false;

    // Tracked background HintBal task — StartWriteBatch / Dispose cancel and drain it so the
    // warmer thread isn't competing with the write batch for CPU or for the snapshot bundle.
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
        WaitForOutstandingWarmups();
        _snapshotBundle.Dispose();
        _warmer.OnExitScope();
    }

    private void CancelHintBal()
    {
        _hintBalCts?.Cancel();
        try { _hintBalTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
            if (logger.IsError) logger.Error("HintBal background task faulted during cancel/drain", ex);
        }
        _hintBalCts?.Dispose();
        _hintBalCts = null;
        _hintBalTask = null;
    }

    // Exposed for tests to observe when the wait loop is entered.
    internal Action? OnWaitingForWarmups;

    private void WaitForOutstandingWarmups()
    {
        if (Volatile.Read(ref _outstandingWarmups) == 0) return;

        OnWaitingForWarmups?.Invoke();

        SpinWait spinWait = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (Volatile.Read(ref _outstandingWarmups) != 0)
        {
            if (stopwatch.ElapsedMilliseconds > 1000)
            {
                ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                if (logger.IsWarn) logger.Warn($"TrieWarmer outstanding jobs ({Volatile.Read(ref _outstandingWarmups)}) did not drain within 1s during scope dispose");
                return;
            }
            spinWait.SpinOnce();
        }
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
            if (_warmer.PushAddressJob(this, address, _hintSequenceId))
                Interlocked.Increment(ref _outstandingWarmups);
        }
    }

    public Task HintBal(BlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
    {
        // Single parallel walk that fuses trie warmup with the (optional) BAL read pass.
        //
        // GetAccount(address) is the only blocking op per account, so we do it once per account
        // and reuse the result for both sides:
        //   - trie warmup needs the StorageRoot to seed a per-account FlatStorageTree before
        //     enqueuing slot jobs for StorageChanges (only writes warm the trie — mirrors HintSet);
        //   - the sink (when provided) receives OnAccountRead + OnStorageRead for both
        //     StorageChanges and StorageReads. SSTORE consults the original value for
        //     EIP-2200 / EIP-3529 gas refund accounting, so changed slots are genuinely read
        //     at runtime; StorageReads ⊥ StorageChanges by BAL construction, so no duplication.
        // Use the pre-sorted array directly when it's already built (always the case for
        // RLP-decoded BALs). If it isn't built, bail out rather than trigger an on-demand sort
        // on a background thread.
        AccountChanges[]? accountChanges = bal.AccountChangesByAddressOrNull;
        if (accountChanges is null || accountChanges.Length == 0) return Task.CompletedTask;
        int accountCount = accountChanges.Length;

        // Cancel and drain any previous in-flight HintBal task before starting a new one —
        // never overlap two warmups on the same scope.
        CancelHintBal();

        _hintBalCts = new CancellationTokenSource();
        CancellationToken token = _hintBalCts.Token;
        int snapshot = _hintSequenceId;

        return _hintBalTask = Task.Run(() =>
        {
            ParallelOptions parallelOptions = new() { CancellationToken = token };
            try
            {
                Parallel.For(0, accountCount, parallelOptions, (i) =>
                {
                    if (token.IsCancellationRequested || _hintSequenceId != snapshot || _pausePrewarmer) return;

                AccountChanges ac = accountChanges[i];
                Address address = ac.Address;

                // Trie warmup: address path.
                if (_warmer.PushAddressJob(this, address, snapshot))
                    Interlocked.Increment(ref _outstandingWarmups);

                // Use the pre-sorted storage arrays directly. If not built, skip that side of
                // the per-account warmup rather than trigger an on-demand sort here.
                SlotChanges[]? storageChanges = ac.StorageChangesOrNull;
                UInt256[]? storageReads = ac.SortedStorageReadsOrNull;
                int storageChangeCount = storageChanges?.Length ?? 0;
                int storageReadCount = storageReads?.Length ?? 0;

                // One GetAccount per account — feeds both the sink and the storage-tree seed.
                Account? account = sink is null && storageChangeCount == 0
                    ? null // no consumer needs the account in this case
                    : _snapshotBundle.GetAccount(address);

                if (sink is not null && sink.StillNeeded(address, out _))
                    sink.OnAccountRead(address, account);

                if (account is null || storageChangeCount + storageReadCount == 0) return;

                Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
                if (storageRoot == Keccak.EmptyTreeHash) return;

                // Per-account non-cached tree just for trie warmup pushes — the main thread's
                // CreateStorageTreeImpl builds its own cached instance on first use.
                FlatStorageTree storageWarmer = storageChangeCount > 0
                    ? new FlatStorageTree(
                        this,
                        _warmer,
                        _snapshotBundle,
                        _configuration,
                        _concurrencyQuota,
                        storageRoot,
                        address,
                        _logManager)
                    : null!;

                int selfDestructIdx = sink is null ? 0 : _snapshotBundle.DetermineSelfDestructSnapshotIdx(address);

                if (storageChanges is not null)
                {
                    foreach (SlotChanges slotChanges in storageChanges)
                    {
                        UInt256 key = slotChanges.Key;
                        // Pair every successful push with an increment — the warmer's
                        // WarmUpStorageTrie always decrements in its finally block.
                        if (_warmer.PushSlotJobMpmc(storageWarmer, key, snapshot))
                            Interlocked.Increment(ref _outstandingWarmups);
                        if (sink is not null) ReadSlotToSink(sink, address, in key, selfDestructIdx);
                    }
                }

                if (sink is not null && storageReads is not null)
                {
                    foreach (UInt256 readKey in storageReads)
                        ReadSlotToSink(sink, address, in readKey, selfDestructIdx);
                }
                });
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void ReadSlotToSink(IWorldStateScopeProvider.IAsyncBalReaderSink sink, Address address, in UInt256 slot, int selfDestructIdx)
    {
        StorageCell cell = new(address, in slot);
        if (!sink.StillNeeded(in cell)) return;
        byte[]? raw = _snapshotBundle.GetSlot(address, in slot, selfDestructIdx);
        sink.OnStorageRead(in cell, raw is null || raw.Length == 0 ? StorageTree.ZeroBytes : raw);
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public int HintSequenceId => _hintSequenceId; // Called by FlatStorageTree

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        try
        {
            if (_hintSequenceId != sequenceId || _pausePrewarmer) return false;

            // Note: tree root not changed after writing batch. Also, not cleared. So the result is not correct.
            // this is just for warming up
            _warmupStateTree.WarmUpPath(address.ToAccountPath.Bytes);

            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _outstandingWarmups);
        }
    }

    internal void IncrementOutstandingWarmups() => Interlocked.Increment(ref _outstandingWarmups);

    internal void DecrementOutstandingWarmups() => Interlocked.Decrement(ref _outstandingWarmups);

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => CreateStorageTreeImpl(address);

    private FlatStorageTree CreateStorageTreeImpl(Address address)
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

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        // Cancel + drain any in-flight HintBal: write batches are CPU-intensive and the warmer
        // task would otherwise race with the writer for the snapshot bundle and for CPU.
        CancelHintBal();
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(long blockNumber)
    {
        _pausePrewarmer = true;

        // Storage tree commits already happened during WriteBatch.Dispose() via
        // StorageTreeBulkWriteBatch(commit: true). Only the state tree needs committing here.
        _stateTree.Commit();

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
