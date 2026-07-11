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
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatWorldStateScope : IWorldStateScopeProvider.IScope, ITrieWarmer.IAddressWarmer
{
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly Lazy<WarmReadPool>? _warmReadPool;
    internal readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly PreservedSparseTrie? _preservedSparseTrie;

    private readonly ConcurrencyController _concurrencyQuota;
    // Constructed only when the configured warmer actually walks the warmup tree
    // (SparseTrieWarmer == Legacy). Other variants (None â†’ DI returns NoopTrieWarmer;
    // SparseProof â†’ uses the proof reader) never touch this field, so allocating a fresh
    // PatriciaTree per scope was pure waste under sparse mode.
    private readonly PatriciaTree? _warmupStateTree;
    private readonly StateTree _stateTree;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = [];
    private ConcurrentDictionary<AddressAsKey, FlatStorageTree?>? _hintWarmStorages;
    private SparseRootComputer? _sparseRootComputer;

    /// <summary>
    /// Internal accessor for the sparse computer. Storage write batches use this to
    /// route per-contract slot changes when sparse storage is authoritative.
    /// </summary>
    internal SparseRootComputer? SparseRootComputerInternal => _sparseRootComputer;

    /// <summary>Exposes the proof reader so storage trees can issue sparse-aware warmup reads.</summary>
    internal ParentStateTrieNodeReader? SparseProofReader => _proofReader;

    /// <summary>
    /// True when sparse storage roots should replace Patricia's: sparse trie is
    /// authoritative for accounts AND SkipPatricia is configured AND not in verification mode
    /// AND the explicit storage-authoritative flag is set. Decoupled from SkipPatricia so we
    /// can isolate sparse storage bugs without disabling sparse account computation.
    /// </summary>
    internal bool UseSparseStorageRoot => _sparseRootComputer is not null
        && _sparseIsAuthoritative
        && _configuration.SparseTrieSkipPatricia
        && _configuration.SparseTrieAuthoritativeStorage
        && !_configuration.SparseTrieVerificationMode;
    private SparseStateTrie? _sparseStateTrie;
    private Hash256? _sparseComputedRoot;
    private bool _sparseIsAuthoritative;
    // Stored for the SparseProof warmer variant: ReadAccountProofs needs both.
    private ParentStateTrieNodeReader? _proofReader;
    private Hash256? _prevStateRoot;
    private bool _isDisposed = false;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmer's ring buffer.
    private volatile int _hintSequenceId = 0;
    private int _outstandingWarmups = 0;
    private StateId _currentStateId;
    internal volatile bool _pausePrewarmer = false;

    private CancellationTokenSource? _hintBalCts;
    private Task? _hintBalTask;

    internal bool IsDisposed => Volatile.Read(ref _isDisposed);

    private readonly SparseAuthoritativeTracker _sparseTracker;

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatCommitTarget commitTarget,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        PreservedSparseTrie? preservedSparseTrie,
        SparseAuthoritativeTracker sparseTracker,
        ILogManager logManager,
        Lazy<WarmReadPool>? warmReadPool = null,
        bool isReadOnly = false)
    {
        _sparseTracker = sparseTracker;
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

        // Legacy is the only warmer variant that walks _warmupStateTree (see WarmUpStateTrie).
        // For None the DI module substitutes NoopTrieWarmer; for SparseProof we issue proof
        // reads directly. In those cases the warmup tree is dead weight.
        if (configuration.SparseTrieWarmer == SparseTrieWarmerVariant.Legacy
            && configuration.TrieWarmerWorkerCount != 0)
        {
            _warmupStateTree = new(
                new StateTrieStoreWarmerAdapter(snapshotBundle),
                logManager
            )
            {
                RootHash = currentStateId.StateRoot.ToCommitment()
            };
        }

        _configuration = configuration;
        _warmReadPool = warmReadPool;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        if (configuration.UseSparseRootComputation && !isReadOnly)
        {
            Hash256 prevRoot = currentStateId.StateRoot.ToCommitment();
            ParentStateTrieNodeReader proofReader = new(snapshotBundle);
            _proofReader = proofReader;
            _prevStateRoot = prevRoot;
            if (configuration.SparseTrieShadowStorageCompare && preservedSparseTrie is not null)
            {
                preservedSparseTrie.DiagLogger = logManager.GetClassLogger<PreservedSparseTrie>();
                // Hardcoded: dump diagnostics for USDT, the contract that diverges at block 22360025.
                SparseRootComputer.DiagDumpForContract ??= new Hash256("0xab14d68802a763f7db875346d03fbf86f137de55814b191c069e721f47474733");
            }

            // M3: cross-block sparse trie reuse via PreservedSparseTrie. Combined with
            // dirty-path-only HashNode and minLen-aware proof reading, the next block
            // skips proof reads for hot paths already revealed in the trie.
            if (preservedSparseTrie is not null)
            {
                _sparseStateTrie = preservedSparseTrie.Take(prevRoot);
                _sparseRootComputer = new SparseRootComputer(_sparseStateTrie, proofReader, prevRoot);
            }
            else
            {
                _sparseRootComputer = new SparseRootComputer(proofReader, prevRoot);
            }

            // M3 LFU retention. Caps come from config; touches happen inside SparseRootComputer
            // before each update batch; the actual collapse runs in Commit before StoreAnchored.
            // Pass the retained-storage-tries budget too: when only THAT is set (the production
            // memory-bound path), the LFUs must still be created so the triggered prune has
            // frequency data to evict by â€” otherwise storage eviction silently no-ops.
            _sparseRootComputer.Trie.SetHotCacheCapacities(
                configuration.SparseTrieMaxHotAccounts,
                configuration.SparseTrieMaxHotSlots,
                configuration.SparseTrieMaxRetainedStorageTries);

            // Sparse drives commit/root only when explicitly opted-in via SparseTrieSkipPatricia
            // AND we've seen 10 consecutive matching roots from this provider (or shadow-compare
            // is off, meaning we have no observed matches but the operator has chosen to trust
            // sparse anyway). Promoting to authoritative when Patricia still runs would silently
            // waste BulkSet work on every block.
            //
            // SparseTrieForceAuthoritative bypasses the 10-block warmup entirely (benchmark-only,
            // see config docs). dotTrace showed the realblocks profile is dominated by the
            // Patricia work that runs ALONGSIDE sparse until promotion; without this switch the
            // benchmark can never observe true sparse-only processing.
            _sparseIsAuthoritative = !configuration.SparseTrieVerificationMode
                && configuration.SparseTrieSkipPatricia
                && (configuration.SparseTrieForceAuthoritative
                    || _sparseTracker.ConsecutiveMatches >= 10);
        }

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        CancelHintBal();
        WaitForOutstandingWarmups();

        // Return the sparse trie to the preserved store if it was checked out but never
        // stored back (e.g., scope disposed without Commit due to reorg or exception).
        // TryStoreCleared is a no-op if Commit already returned ownership.
        if (_preservedSparseTrie is not null && _sparseStateTrie is not null)
        {
            _preservedSparseTrie.TryStoreCleared(_sparseStateTrie);
            _sparseStateTrie = null;
        }

        _sparseRootComputer?.Dispose();
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

                // Observability (cheap, set once per block): root compute time + proof activity.
                Metrics.SparseRootComputeTime.Observe(sw.ElapsedMilliseconds);
                Metrics.SparseProofNodesRead += _sparseRootComputer.LastProofNodeCount;
                if (_sparseRootComputer.LastRetryCount > 0)
                    Metrics.SparseProofRetries += _sparseRootComputer.LastRetryCount;

                if (!_sparseIsAuthoritative)
                {
                    // Validation mode: run Patricia too and compare
                    _stateTree.UpdateRootHash();

                    if (_sparseComputedRoot == _stateTree.RootHash)
                    {
                        int matchCount = _sparseTracker.RecordMatch();
                        int consecutive = _sparseTracker.ConsecutiveMatches;
                        if (matchCount % 100 == 1 || matchCount <= 5)
                        {
                            ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                            if (logger.IsInfo) logger.Info(
                                $"SPARSE ROOT MATCH #{matchCount}! Root={_sparseComputedRoot}, " +
                                $"SparseTime={sw.ElapsedMilliseconds}ms (proof={_sparseRootComputer.LastProofReadMs}ms " +
                                $"reveal={_sparseRootComputer.LastRevealMs}ms update={_sparseRootComputer.LastUpdateLeavesMs}ms " +
                                $"compute={_sparseRootComputer.LastComputeRootMs}ms, retries={_sparseRootComputer.LastRetryCount}, " +
                                $"proofNodes={_sparseRootComputer.LastProofNodeCount}, accounts={_sparseRootComputer.AccountChangeCount}), " +
                                $"Consecutive={consecutive}, " +
                                $"Totals: match={matchCount} mismatch={_sparseTracker.MismatchCount} fail={_sparseTracker.FailCount}");
                        }
                    }
                    else
                    {
                        int mismatchCount = _sparseTracker.RecordMismatch();
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
                    int matchCount = _sparseTracker.RecordMatch();
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
                int failCount = _sparseTracker.RecordFail();
                ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                if (logger.IsWarn) logger.Warn(
                    $"SPARSE ROOT FAIL #{failCount}! Exception={ex.GetType().Name}: {ex.Message}, " +
                    $"Totals: match={_sparseTracker.MatchCount} mismatch={_sparseTracker.MismatchCount} fail={failCount}");
                _sparseComputedRoot = null;
                _sparseIsAuthoritative = false;

                // In SkipPatricia (M3) mode, Patricia BulkSet was never called, so falling back
                // to Patricia would produce a stale/empty root. Propagate the exception.
                if (_configuration.SparseTrieSkipPatricia)
                    throw;

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

    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
    {
        int accountCount = bal.AccountChanges.Count;
        if (accountCount == 0) return Task.CompletedTask;

        // Copy the span into a pooled array so the Task.Run body can capture it.
        ArrayPoolList<ReadOnlyAccountChanges> accountChanges = new(bal.AccountChanges.AsSpan());

        CancelHintBal();

        _hintBalCts = new CancellationTokenSource();
        CancellationToken token = _hintBalCts.Token;
        int snapshot = _hintSequenceId;

        return _hintBalTask = Task.Run(() =>
        {
            ParallelOptions parallelOptions = new() { CancellationToken = token };

            Account?[]? accounts = sink is null ? null : new Account?[accountCount];
            int[]? selfDestructIdxs = sink is null ? null : new int[accountCount];

            try
            {
                // Phase 1: trie warmup + GetAccount + sink.OnAccountRead. Sink slot reads are
                // deferred to phase 2 so one huge account doesn't bottleneck a single worker.
                Parallel.For(0, accountCount, parallelOptions, (i) =>
                {
                    if (token.IsCancellationRequested || _hintSequenceId != snapshot || _pausePrewarmer) return;

                    ReadOnlyAccountChanges ac = accountChanges[i];
                    Address address = ac.Address;

                    if (_snapshotBundle.ShouldQueuePrewarm(address)
                        && _warmer.PushAddressJob(this, address, snapshot))
                        Interlocked.Increment(ref _outstandingWarmups);

                    ReadOnlySlotChanges[] storageChanges = ac.StorageChanges;
                    int storageChangeCount = storageChanges.Length;

                    Account? account = sink is null && storageChangeCount == 0
                        ? null
                        : _snapshotBundle.GetAccount(address);

                    if (sink is not null && sink.StillNeeded(address, out _))
                        sink.OnAccountRead(address, account);

                    if (account is null) return;
                    Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
                    if (storageRoot == Keccak.EmptyTreeHash) return;

                    if (storageChangeCount > 0)
                    {
                        FlatStorageTree storageWarmer = new(
                            this,
                            _warmer,
                            _snapshotBundle,
                            _configuration,
                            _concurrencyQuota,
                            storageRoot,
                            address,
                            _logManager);

                        foreach (ReadOnlySlotChanges slotChanges in storageChanges)
                        {
                            UInt256 key = slotChanges.Key;
                            if (_snapshotBundle.ShouldQueuePrewarm(address, key)
                                && _warmer.PushSlotJobMpmc(storageWarmer, key, snapshot))
                                Interlocked.Increment(ref _outstandingWarmups);
                        }
                    }

                    if (accounts is not null)
                    {
                        accounts[i] = account;
                        selfDestructIdxs![i] = _snapshotBundle.DetermineSelfDestructSnapshotIdx(address);
                    }
                });

                if (sink is not null) RunSinkSlotReads(accountChanges, accounts!, selfDestructIdxs!, sink, parallelOptions);
            }
            catch (OperationCanceledException) { }
            finally
            {
                accountChanges.Dispose();
            }
        }, token);
    }

    private void RunSinkSlotReads(
        ArrayPoolList<ReadOnlyAccountChanges> accountChanges,
        Account?[] accounts,
        int[] selfDestructIdxs,
        IWorldStateScopeProvider.IAsyncBalReaderSink sink,
        ParallelOptions parallelOptions)
    {
        // Read-only providers have no pool; sinks are only passed on the writable block-processing path.
        if (_warmReadPool is null) return;

        int totalSlots = 0;
        for (int i = 0; i < accountChanges.Count; i++)
        {
            if (accounts[i] is null) continue;
            totalSlots += accountChanges[i].StorageChanges.Length
                       + accountChanges[i].StorageReads.Length;
        }

        if (totalSlots == 0) return;

        using ArrayPoolList<(Address Address, int SelfDestructIdx, UInt256 Slot)> jobs = new(totalSlots, totalSlots);
        int idx = 0;
        for (int i = 0; i < accountChanges.Count; i++)
        {
            if (accounts[i] is null) continue;
            ReadOnlyAccountChanges ac = accountChanges[i];
            Address address = ac.Address;
            int selfDestructIdx = selfDestructIdxs[i];
            foreach (ReadOnlySlotChanges slotChanges in ac.StorageChanges)
                jobs[idx++] = (address, selfDestructIdx, slotChanges.Key);
            foreach (UInt256 readKey in ac.StorageReads)
                jobs[idx++] = (address, selfDestructIdx, readKey);
        }

        // Lazy materialisation: this is the only call site that needs the pool, so chains/forks
        // that never see a BAL never allocate the dedicated reader threads.
        WarmReadPool pool = _warmReadPool.Value;
        int workers = Math.Min(pool.MaxConcurrency, Math.Max(1, idx / 64));

        pool.Run(idx, workers, j =>
        {
            if (_pausePrewarmer) return;
            (Address address, int selfDestructIdx, UInt256 slot) = jobs[j];
            ReadSlotToSink(sink, address, in slot, selfDestructIdx);
        }, parallelOptions.CancellationToken);
    }

    private void ReadSlotToSink(IWorldStateScopeProvider.IAsyncBalReaderSink sink, Address address, in UInt256 slot, int selfDestructIdx)
    {
        StorageCell cell = new(address, in slot);
        if (!sink.StillNeeded(in cell)) return;
        byte[]? raw = _snapshotBundle.GetSlot(address, in slot, selfDestructIdx);
        sink.OnStorageRead(in cell, raw is null || raw.Length == 0 ? StorageTree.ZeroBytes : raw);
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public int HintSequenceId => _hintSequenceId;

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        try
        {
            if (_hintSequenceId != sequenceId || _pausePrewarmer) return false;
            if (!_snapshotBundle.TryLeaseReadOnlyBundle()) return false;

            try
            {
                // Variant selection:
                //   Legacy â€” walks Patricia and warms the transient cache via the adapter.
                //   SparseProof â€” EXPERIMENTAL. Runs a full root-to-leaf proof read for a single
                //     target and DISCARDS the decoded result, keeping only the DB/OS page-cache
                //     warming side effect. The discard is REQUIRED, not laziness: this method runs on
                //     trie-warmer WORKER threads, while the block-processing thread mutates the same
                //     preserved SparseStateTrie during ComputeStateRoot. SparsePatriciaTree.RevealNodes
                //     is single-writer with no locking, so revealing the fetched proof here would race
                //     the main reveal/update and corrupt the arena (intermittent wrong roots). A real
                //     sparse-native prefetcher must therefore route proofs to a SINGLE consumer that
                //     owns all reveals â€” i.e. the M4 SparseTrieTask. Finding 6 is thus blocked on M4;
                //     they are coupled, not independent. Until then SparseProof is only a DB warmer and
                //     the prewarm-off benchmark (+69% without any warmer) shows the Legacy Patricia
                //     warmer remains load-bearing, so it stays the default.
                //   None â€” gated at DI by NoopTrieWarmer (never reaches this method).
                // Flat-native warmer: warm the actual account read path (SnapshotBundle -> persistence
                // -> RocksDB) the EVM will hit, with no Patricia decode. Mirrors the storage-side Flat
                // variant in FlatStorageTree.WarmUpStorageTrie.
                if (_configuration.SparseTrieWarmer == SparseTrieWarmerVariant.Flat)
                {
                    _ = _snapshotBundle.GetAccount(address);
                    return true;
                }

                if (_configuration.SparseTrieWarmer == SparseTrieWarmerVariant.SparseProof
                    && _proofReader is not null && _prevStateRoot is not null)
                {
                    _ = Nethermind.Trie.Sparse.MultiProofReader.ReadAccountProofs(
                        _proofReader, _prevStateRoot, [Keccak.Compute(address.Bytes)]);
                }
                else if (_warmupStateTree is not null)
                {
                    _warmupStateTree.WarmUpPath(address.ToAccountPath.Bytes);
                }
                return true;
            }
            finally
            {
                _snapshotBundle.ReleaseReadOnlyBundleLease();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _outstandingWarmups);
        }
    }

    internal void IncrementOutstandingWarmups() => Interlocked.Increment(ref _outstandingWarmups);

    internal void DecrementOutstandingWarmups() => Interlocked.Decrement(ref _outstandingWarmups);

    public void HintWarmAccount(in ValueAddress address)
    {
        if (IsDisposed || _pausePrewarmer) return;
        // The managed Address is materialized only after the dedupe bloom passes, so the
        // allocation happens at most once per account per block.
        if (_snapshotBundle.ShouldQueuePrewarm(address)
            && _warmer.PushAddressJob(this, address.ToAddress(), _hintSequenceId))
            Interlocked.Increment(ref _outstandingWarmups);
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index)
    {
        if (IsDisposed || _pausePrewarmer) return;
        if (!_snapshotBundle.ShouldQueuePrewarm(address, index)) return;

        FlatStorageTree? tree = GetOrCreateHintWarmStorageTree(address.ToAddress());
        if (tree is not null && _warmer.PushSlotJobMpmc(tree, index, _hintSequenceId))
            Interlocked.Increment(ref _outstandingWarmups);
    }

    private FlatStorageTree? GetOrCreateHintWarmStorageTree(Address address) =>
        GetHintWarmStorages().GetOrAdd(address, static (key, scope) =>
        {
            Hash256 storageRoot = scope._snapshotBundle.GetAccount(key.Value)?.StorageRoot ?? Keccak.EmptyTreeHash;
            return storageRoot == Keccak.EmptyTreeHash
                ? null
                : new FlatStorageTree(
                    scope,
                    scope._warmer,
                    scope._snapshotBundle,
                    scope._configuration,
                    scope._concurrencyQuota,
                    storageRoot,
                    key.Value,
                    scope._logManager);
        }, this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ConcurrentDictionary<AddressAsKey, FlatStorageTree?> GetHintWarmStorages()
    {
        ConcurrentDictionary<AddressAsKey, FlatStorageTree?>? storages = Volatile.Read(ref _hintWarmStorages);
        return storages ?? InitializeHintWarmStorages();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ConcurrentDictionary<AddressAsKey, FlatStorageTree?> InitializeHintWarmStorages()
    {
        ConcurrentDictionary<AddressAsKey, FlatStorageTree?> newStorages = new();
        return Interlocked.CompareExchange(ref _hintWarmStorages, newStorages, null) ?? newStorages;
    }

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
        CancelHintBal();
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(ulong blockNumber)
    {
        _pausePrewarmer = true;

        bool usedSparseCommit = false;
        if (_sparseIsAuthoritative && _sparseComputedRoot is not null && _sparseRootComputer is not null)
        {
            try
            {
                SparseStateTrie trie = _sparseRootComputer.Trie;
                SparseTrieSnapshotCommitter.CommitAccountTrie(trie.AccountTrie.Subtrie, _snapshotBundle);

                // P1: persist sparse storage trie nodes. Walk only contracts whose storage
                // changed this block (DirtyStorageAccountHashes), not every retained storage
                // trie â€” preserved-trie state across blocks grows the StorageTries dictionary
                // without bound until pruning is added (M3+), so iterating it on every commit
                // is O(retained contracts). Block N+1's proof reader needs the persisted nodes
                // only for the contracts block N actually touched.
                foreach (Hash256 dirtyAccount in _sparseRootComputer.DirtyStorageAccountHashes())
                {
                    if (trie.StorageTries.TryGetValue(dirtyAccount, out SparsePatriciaTree? dirtyTrie))
                    {
                        SparseTrieSnapshotCommitter.CommitStorageTrie(dirtyTrie.Subtrie, _snapshotBundle, dirtyAccount);
                    }
                }
                usedSparseCommit = true;
            }
            catch (Exception ex)
            {
                ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                if (logger.IsWarn) logger.Warn($"SparseTrieSnapshotCommitter failed: {ex.Message}.");
                _sparseTracker.ResetConsecutive();

                // CRITICAL: in SkipPatricia mode, the Patricia tree was never populated via BulkSet,
                // so falling back to Patricia.Commit() would persist STALE state under the sparse
                // root. Mirrors the UpdateRootHash skip-mode rethrow contract.
                if (_configuration.SparseTrieSkipPatricia) throw;
            }
        }

        if (!usedSparseCommit)
        {
            _stateTree.Commit();
        }

        _storages.Clear();
        _hintWarmStorages?.Clear();

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

        // M3: anchor the sparse trie for cross-block reuse if the computed root matches.
        if (_sparseRootComputer is not null)
        {
            Hash256 newRoot = newStateId.StateRoot.ToCommitment();

            if (_preservedSparseTrie is not null && _sparseStateTrie is not null)
            {
                if (_sparseComputedRoot is not null && _sparseComputedRoot == newRoot)
                {
                    // Prune policy â€” two independent triggers, both off by default (caps =
                    // int.MaxValue), evaluated against the trie we are about to anchor:
                    //
                    //   1. LFU per-block prune: fires every commit when either hot-cap is finite.
                    //      Aggressive; only sensible for workloads with strong hot-key locality.
                    //      Measured 7.5x slower on realblocks (evicts the working set), so it
                    //      stays opt-in.
                    //
                    //   2. Triggered memory-bound prune: fires only on commits where the retained
                    //      storage-trie count exceeds SparseTrieMaxRetainedStorageTries. This is
                    //      the production-safe bound â€” warm steady state pays nothing because the
                    //      check is a single int compare and the prune body runs only under real
                    //      memory pressure. When it does fire it uses the LFU caps to decide what
                    //      to retain; if those are left at int.MaxValue we fall back to retaining
                    //      the budget itself so the prune actually frees memory.
                    bool lfuPruneEnabled = _configuration.SparseTrieMaxHotAccounts < int.MaxValue
                                        || _configuration.SparseTrieMaxHotSlots < int.MaxValue;

                    int retainedStorageTries = _sparseStateTrie.StorageTries.Count;
                    bool overMemoryBudget = retainedStorageTries > _configuration.SparseTrieMaxRetainedStorageTries;

                    bool shouldPrune = lfuPruneEnabled || overMemoryBudget;
                    int evictedStorageTries = 0;
                    if (shouldPrune)
                    {
                        // When only the memory-budget trigger fired, the operator may not have set
                        // finite LFU caps. Derive retention from the budget so the prune is
                        // meaningful: keep at most budget accounts/contracts and a generous
                        // per-contract slot allowance, rather than pruning to int.MaxValue (a no-op).
                        int hotAccounts = _configuration.SparseTrieMaxHotAccounts;
                        int hotSlots = _configuration.SparseTrieMaxHotSlots;
                        if (overMemoryBudget && !lfuPruneEnabled)
                        {
                            hotAccounts = _configuration.SparseTrieMaxRetainedStorageTries;
                            hotSlots = _configuration.SparseTrieMaxRetainedStorageTries;
                        }

                        try
                        {
                            evictedStorageTries = _sparseStateTrie.Prune(hotAccounts, hotSlots);
                        }
                        catch (Exception ex)
                        {
                            ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                            if (logger.IsWarn) logger.Warn($"Sparse trie prune failed: {ex.Message}. " +
                                $"Falling back to Cleared store â€” next block starts cold.");
                            _preservedSparseTrie.StoreCleared(_sparseStateTrie);
                            _sparseStateTrie = null;
                            _sparseRootComputer.Dispose();
                            _sparseRootComputer = null;
                            _sparseComputedRoot = null;
                            return;
                        }
                    }

                    // Observability for the cross-block cache. With pruning off the retained
                    // storage-trie count grows unbounded (one arena per contract ever touched).
                    // Logged at Debug every commit, plus an Info sample every 250 blocks so the
                    // natural working-set high-water is visible in benchmark/operator logs (where
                    // Debug is usually off) without per-block spam â€” this is the number that tells
                    // you how to size SparseTrieMaxRetainedStorageTries.
                    if (evictedStorageTries > 0) Metrics.SparseEvictedStorageTries += evictedStorageTries;

                    ILogger sizeLogger = _logManager.GetClassLogger<FlatWorldStateScope>();
                    bool sample = blockNumber % 250 == 0;
                    // Refresh the cross-block cache gauges once per sample (cheap arena-count walk;
                    // avoid doing it every block). These let operators watch retained memory grow
                    // and size SparseTrieMaxRetainedStorageTries against a budget.
                    if (sample)
                    {
                        SparseStateTrie.CacheSize gauge = _sparseStateTrie.GetCacheSize();
                        Metrics.SparseRetainedStorageTries = gauge.StorageTrieCount;
                        Metrics.SparseAccountArenaNodes = gauge.AccountArenaNodes;
                        Metrics.SparseStorageArenaNodes = gauge.StorageArenaNodes;
                    }
                    if (sizeLogger.IsDebug || (sample && sizeLogger.IsInfo))
                    {
                        SparseStateTrie.CacheSize cs = _sparseStateTrie.GetCacheSize();
                        string msg =
                            $"Sparse cross-block cache @ block {blockNumber}: storageTries={cs.StorageTrieCount}, " +
                            $"accountArenaNodes={cs.AccountArenaNodes}, storageArenaNodes={cs.StorageArenaNodes}, " +
                            $"lfuPrune={(lfuPruneEnabled ? "on" : "off")}, " +
                            $"memTrigger={(overMemoryBudget ? "FIRED" : "idle")}, " +
                            $"evictedStorageTries={evictedStorageTries}";
                        if (sample && sizeLogger.IsInfo) sizeLogger.Info(msg);
                        else sizeLogger.Debug(msg);
                    }

                    _preservedSparseTrie.StoreAnchored(_sparseStateTrie, newRoot);
                }
                else
                    _preservedSparseTrie.StoreCleared(_sparseStateTrie);
                _sparseStateTrie = null;
            }

            _sparseRootComputer.Dispose();
            _sparseRootComputer = null;
            _sparseComputedRoot = null;
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

                // Feed Patricia BulkSet UNLESS authoritative + SkipPatricia configured (M3 mode).
                // In M3 mode, sparse is the only computation; Patricia BulkSet is skipped for perf.
                bool skipPatriciaBulkSet = scope._configuration.SparseTrieSkipPatricia
                    && scope._sparseIsAuthoritative
                    && !scope._configuration.SparseTrieVerificationMode;
                if (!skipPatriciaBulkSet)
                {
                    using StateTree.StateTreeBulkSetter stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count);
                    foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                    {
                        stateSetter.Set(kv.Key, kv.Value);
                    }
                }

                // Feed account changes to SparseRootComputer. ValueKeccak.Compute returns the
                // value-type hash directly so the per-account hash never allocates a Hash256
                // (class). At 10k+ accounts/block this is one of the dominant alloc sources.
                if (scope._sparseRootComputer is not null)
                {
                    Dictionary<ValueHash256, LeafUpdate> sparseAccountUpdates = new(_dirtyAccounts.Count);
                    foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                    {
                        ValueHash256 hashedAddr = ValueKeccak.Compute(kv.Key.Value.Bytes);
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
