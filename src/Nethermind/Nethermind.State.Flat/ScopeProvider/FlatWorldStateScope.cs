// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatWorldStateScope : IWorldStateScopeProvider.IScope, ITrieWarmer.IAddressWarmer
{
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly PreservedSparseTrie? _preservedSparseTrie;

    private readonly ConcurrencyController _concurrencyQuota;
    private readonly PatriciaTree _warmupStateTree;
    private readonly StateTree _stateTree;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = [];
    private SparseRootComputer? _sparseRootComputer;
    private SparseStateTrie? _sparseStateTrie;
    private Hash256? _sparseComputedRoot;
    private bool _sparseIsAuthoritative;
    private bool _isDisposed = false;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmer's ring buffer.
    private int _hintSequenceId = 0;
    private int _outstandingWarmups = 0;
    private StateId _currentStateId;
    internal bool _pausePrewarmer = false;

    private static int _sparseMatchCount;
    private static int _sparseMismatchCount;
    private static int _sparseFailCount;
    private static int _consecutiveMatches;

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatCommitTarget commitTarget,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        PreservedSparseTrie? preservedSparseTrie,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        CodeDb = codeDb;
        _commitTarget = commitTarget;
        _preservedSparseTrie = preservedSparseTrie;

        _concurrencyQuota = new ConcurrencyController(Environment.ProcessorCount);
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

        if (configuration.UseSparseRootComputation && !isReadOnly)
        {
            Hash256 prevRoot = currentStateId.StateRoot.ToCommitment();
            ParentStateTrieNodeReader proofReader = new(snapshotBundle);

            if (preservedSparseTrie is not null)
            {
                _sparseStateTrie = preservedSparseTrie.Take(prevRoot);
                _sparseRootComputer = new SparseRootComputer(_sparseStateTrie, proofReader, prevRoot);
            }
            else
            {
                _sparseRootComputer = new SparseRootComputer(proofReader, prevRoot);
            }

            // After enough consecutive matches, skip Patricia UpdateRootHash (unless verification mode is on)
            _sparseIsAuthoritative = !configuration.SparseTrieVerificationMode
                && Volatile.Read(ref _consecutiveMatches) >= 10;
        }

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        WaitForOutstandingWarmups();

        // Return the sparse trie to the preserved store if it was checked out but never
        // stored back (e.g., scope disposed without Commit due to reorg or exception).
        if (_preservedSparseTrie is not null && _sparseStateTrie is not null)
        {
            try { _preservedSparseTrie.StoreCleared(_sparseStateTrie); }
            catch (InvalidOperationException) { }
            _sparseStateTrie = null;
        }

        _sparseRootComputer?.Dispose();
        _snapshotBundle.Dispose();
        _warmer.OnExitScope();
    }

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

    public Hash256 RootHash => _sparseComputedRoot ?? _stateTree.RootHash;

    public void UpdateRootHash()
    {
        if (_sparseRootComputer is not null)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                _sparseComputedRoot = _sparseRootComputer.ComputeStateRoot();
                sw.Stop();

                if (!_sparseIsAuthoritative)
                {
                    // Validation mode: run Patricia too and compare
                    _stateTree.UpdateRootHash();

                    if (_sparseComputedRoot == _stateTree.RootHash)
                    {
                        int matchCount = Interlocked.Increment(ref _sparseMatchCount);
                        int consecutive = Interlocked.Increment(ref _consecutiveMatches);
                        if (matchCount % 100 == 1 || matchCount <= 5)
                        {
                            ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                            if (logger.IsInfo) logger.Info(
                                $"SPARSE ROOT MATCH #{matchCount}! Root={_sparseComputedRoot}, " +
                                $"SparseTime={sw.ElapsedMilliseconds}ms, Consecutive={consecutive}, " +
                                $"Totals: match={matchCount} mismatch={_sparseMismatchCount} fail={_sparseFailCount}");
                        }
                    }
                    else
                    {
                        int mismatchCount = Interlocked.Increment(ref _sparseMismatchCount);
                        Interlocked.Exchange(ref _consecutiveMatches, 0);
                        ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                        if (logger.IsWarn) logger.Warn(
                            $"SPARSE ROOT MISMATCH #{mismatchCount}! Patricia={_stateTree.RootHash}, Sparse={_sparseComputedRoot}, " +
                            $"SparseTime={sw.ElapsedMilliseconds}ms, Accounts={_sparseRootComputer.AccountChangeCount}, " +
                            $"ProofNodes={_sparseRootComputer.LastProofNodeCount}, PrevRoot={_sparseRootComputer.PreviousRoot}. " +
                            $"Falling back to Patricia.");
                        _sparseComputedRoot = null;
                    }
                }
                else
                {
                    // Authoritative mode: sparse root is the answer, skip Patricia UpdateRootHash
                    int matchCount = Interlocked.Increment(ref _sparseMatchCount);
                    Interlocked.Increment(ref _consecutiveMatches);
                    if (matchCount % 500 == 1)
                    {
                        ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                        if (logger.IsInfo) logger.Info(
                            $"SPARSE ROOT (authoritative) #{matchCount}: Root={_sparseComputedRoot}, " +
                            $"SparseTime={sw.ElapsedMilliseconds}ms");
                    }
                }
            }
            catch (Exception ex)
            {
                int failCount = Interlocked.Increment(ref _sparseFailCount);
                Interlocked.Exchange(ref _consecutiveMatches, 0);
                ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                if (logger.IsWarn) logger.Warn(
                    $"SPARSE ROOT FAIL #{failCount}! Exception={ex.GetType().Name}: {ex.Message}, " +
                    $"Totals: match={_sparseMatchCount} mismatch={_sparseMismatchCount} fail={failCount}");
                _sparseComputedRoot = null;
                _sparseIsAuthoritative = false;

                // Fallback: run Patricia
                _stateTree.UpdateRootHash();
            }
        }
        else
        {
            _stateTree.UpdateRootHash();
        }
    }

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

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public int HintSequenceId => _hintSequenceId;

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        try
        {
            if (_hintSequenceId != sequenceId || _pausePrewarmer) return false;
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

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
        new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());

    public void Commit(long blockNumber)
    {
        _pausePrewarmer = true;

        bool usedSparseCommit = false;
        if (_sparseIsAuthoritative && _sparseComputedRoot is not null && _sparseRootComputer is not null)
        {
            try
            {
                SparseStateTrie trie = _sparseRootComputer.Trie;
                SparseTrieSnapshotCommitter.CommitAccountTrie(trie.AccountTrie.Subtrie, _snapshotBundle);
                usedSparseCommit = true;
            }
            catch (Exception ex)
            {
                ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                if (logger.IsWarn) logger.Warn($"SparseTrieSnapshotCommitter failed: {ex.Message}. Falling back to Patricia Commit.");
                Interlocked.Exchange(ref _consecutiveMatches, 0);
            }
        }

        if (!usedSparseCommit)
        {
            _stateTree.Commit();
        }

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

        // M3: Store sparse trie back for cross-block reuse, then create fresh computer for next block
        if (_sparseRootComputer is not null)
        {
            Hash256 newRoot = newStateId.StateRoot.ToCommitment();

            if (_preservedSparseTrie is not null && _sparseStateTrie is not null)
            {
                if (_sparseComputedRoot is not null)
                    _preservedSparseTrie.StoreAnchored(_sparseStateTrie, newRoot);
                else
                    _preservedSparseTrie.StoreCleared(_sparseStateTrie);

                _sparseStateTrie = _preservedSparseTrie.Take(newRoot);
            }

            _sparseRootComputer.Dispose();
            ParentStateTrieNodeReader proofReader = new(_snapshotBundle);

            _sparseRootComputer = _sparseStateTrie is not null
                ? new SparseRootComputer(_sparseStateTrie, proofReader, newRoot)
                : new SparseRootComputer(proofReader, newRoot);

            _sparseComputedRoot = null;
            _sparseIsAuthoritative = !_configuration.SparseTrieVerificationMode
                && Volatile.Read(ref _consecutiveMatches) >= 10;
        }

        _pausePrewarmer = false;
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
            _dirtyAccounts[key] = account;
            scope._snapshotBundle.SetAccount(key, account);

            if (account is null)
            {
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
                        using IWorldStateScopeProvider.IStorageWriteBatch wb = CreateStorageWriteBatch(key.Value, 0);
                        wb.Clear();
                        continue;
                    }
                    account = account.WithChangedStorageRoot(storageRoot);
                    _dirtyAccounts[key] = account;

                    scope._snapshotBundle.SetAccount(key, account);

                    Address address = key.Value;
                    OnAccountUpdated?.Invoke(address, new IWorldStateScopeProvider.AccountUpdated(address, account));
                    if (logger.IsTrace) Trace(address, storageRoot, account);
                }

                // Always feed Patricia BulkSet — even in authoritative mode, we need
                // the Patricia tree populated as a safety net for fallback if the sparse
                // trie throws during UpdateRootHash or Commit.
                {
                    using StateTree.StateTreeBulkSetter stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count);
                    foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                    {
                        stateSetter.Set(kv.Key, kv.Value);
                    }
                }

                // Feed account changes to SparseRootComputer
                if (scope._sparseRootComputer is not null)
                {
                    Dictionary<Hash256, LeafUpdate> sparseAccountUpdates = new(_dirtyAccounts.Count);
                    foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                    {
                        Hash256 hashedAddr = Keccak.Compute(kv.Key.Value.Bytes);
                        if (kv.Value is null)
                        {
                            sparseAccountUpdates[hashedAddr] = LeafUpdate.Deleted();
                        }
                        else
                        {
                            byte[] rlp = Nethermind.Serialization.Rlp.AccountDecoder.Instance.Encode(kv.Value).Bytes;
                            sparseAccountUpdates[hashedAddr] = LeafUpdate.Changed(rlp);
                        }
                    }
                    scope._sparseRootComputer.SetAccountChanges(sparseAccountUpdates);
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
