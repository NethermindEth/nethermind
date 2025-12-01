// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State.Flat;

public class FlatDiffRepository : IFlatDiffRepository
{
    private Lock _repoLock = new Lock(); // Note: lock is for proteccting in memory and compacted states only
    private readonly ICanonicalStateRootFinder _stateRootFinder;
    private Dictionary<StateId, Snapshot> _compactedKnownStates = new();
    private InMemorySnapshotStore _inMemorySnapshotStore;
    private ObjectPool<SnapshotContent> _snapshotPool = new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(false));
    private ObjectPool<SnapshotContent> _compactedSnapshotPool = new DefaultObjectPool<SnapshotContent>(new SnapshotContentPolicy(false));

    private class SnapshotContentPolicy(bool allow) : IPooledObjectPolicy<SnapshotContent>
    {
        public SnapshotContent Create()
        {
            return new SnapshotContent(
                Accounts: new Dictionary<AddressAsKey, Account?>(),
                Storages: new ConcurrentDictionary<(AddressAsKey, UInt256), byte[]?>(),
                SelfDestructedStorageAddresses: new ConcurrentDictionary<AddressAsKey, bool>(),
                TrieNodes: new ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode>()
            );
        }

        public bool Return(SnapshotContent obj)
        {
            obj.Reset();
            return allow;
        }
    }

    private IPersistence _persistence;
    private int _boundary;

    private Channel<StateId> _compactorJobs;
    private long _compactSize;
    private readonly bool _inlineCompaction;
    private ILogger _logger;
    private StateId _currentPersistedState;

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public record Configuration(
        int MaxInFlightCompactJob = 32,
        int CompactSize = 64,
        int ConcurrentCompactor = 4,
        int Boundary = 128,
        long TrieCacheMemoryTarget = 2_000_000_000,
        bool VerifyWithTrie = false,
        bool ReadWithTrie = false,
        bool InlineCompaction = false
    )
    {
    }

    public FlatDiffRepository(
        IProcessExitSource exitSource,
        ICanonicalStateRootFinder stateRootFinder,
        IPersistence persistedPersistence,
        ILogManager logManager,
        Configuration? config = null)
    {
        if (config is null) config = new Configuration();
        _inMemorySnapshotStore = new InMemorySnapshotStore();
        _persistence = persistedPersistence;
        _compactSize = config.CompactSize;
        _inlineCompaction = config.InlineCompaction;
        _stateRootFinder = stateRootFinder;
        _logger = logManager.GetClassLogger<FlatDiffRepository>();

        _compactorJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);
        _boundary = config.Boundary;

        using var reader = LeaseReader();
        _currentPersistedState = reader.CurrentState;
        _trieNodeCache = new TrieNodeCache(config.TrieCacheMemoryTarget, logManager);

        for (int i = 0; i < config.ConcurrentCompactor; i++)
        {
            _ = RunCompactor(doPersist: i == 0, exitSource.Token);
        }
    }

    private Lock _readerCacheLock = new Lock();
    private RefCountingPersistenceReader? _cachedReader = null;
    private readonly TrieNodeCache _trieNodeCache;

    private RefCountingPersistenceReader LeaseReader()
    {
        using var _ = _readerCacheLock.EnterScope();
        var cachedReader = _cachedReader;
        if (cachedReader is null)
        {
            _cachedReader = cachedReader = new RefCountingPersistenceReader(
                _persistence.CreateReader()
            );
        }

        cachedReader.AcquireLease();
        return cachedReader;
    }

    private void ClearReaderCache()
    {
        using var _ = _readerCacheLock.EnterScope();
        RefCountingPersistenceReader? cachedReader = _cachedReader;
        _cachedReader = null;
        cachedReader?.Dispose();
    }

    private async Task RunCompactor(bool doPersist, CancellationToken cancellationToken)
    {
        await foreach (var stateId in _compactorJobs.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                CompactLevel(stateId);
                if (doPersist) await CleanIfNeeded();
            }
            catch (Exception ex)
            {
                _logger.Error("Compact job failed", ex);
                throw;
            }
        }
    }

    private async Task NotifyWhenSlow(string name, Action closure)
    {
        Task jobTask = Task.Run(() =>
        {
            try
            {
                closure();
            }
            catch (Exception ex)
            {
                _logger.Error($"job {name} failed", ex);
                Environment.Exit(1);
                throw;
            }
        });
        Task waiterTask = Task.Run(async () =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(1000);
                if (jobTask.IsCompleted) break;
                _logger.Info($"Task {name} took {sw.Elapsed}");
            }
        });

        await Task.WhenAny(jobTask, waiterTask);
    }

    private void RunCompactJob(StateId stateId)
    {
        CompactLevel(stateId);
        CleanIfNeeded().Wait();
    }

    private void CompactLevel(StateId stateId)
    {
        try
        {
            if (_compactSize <= 1) return; // Disabled
            long blockNumber = stateId.blockNumber;
            if (blockNumber == 0) return;
            if (blockNumber % _compactSize != 0)
            {
                using (_repoLock.EnterScope())
                {
                    StateId? last = _inMemorySnapshotStore.GetLast();
                    if (last != null && last.Value.blockNumber - blockNumber > 1)
                    {
                        // To slow. Just skip this block number.
                        return;
                    }
                }
            }

            long startingBlockNumber = ((blockNumber - 1) / _compactSize) * _compactSize;

            using SnapshotBundle gatheredCache = GatherCache(stateId, startingBlockNumber);
            if (gatheredCache.SnapshotCount == 1)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Compacting {stateId}");
            Snapshot snapshot = gatheredCache.CompactToKnownState(_compactedSnapshotPool);

            using (_repoLock.EnterScope())
            {
                if (_logger.IsDebug) _logger.Debug($"Compacted {gatheredCache.SnapshotCount} to {stateId}");
                _compactedKnownStates[stateId] = snapshot;

                if (stateId.blockNumber % _compactSize != 0)
                {
                    // Save memory
                    foreach (var id in _inMemorySnapshotStore.GetStatesAtBlockNumber(stateId.blockNumber - _compactSize))
                    {
                        RemoveAndReleaseCompactedKnownState(id);
                    }
                }
            }
        }
        catch (Exception e)
        {
            _logger.Error($"Compactor failed {e}");
        }
    }

    public SnapshotBundle? GatherReaderAtBaseBlock(StateId baseBlock)
    {
        // TODO: Throw if not enough or return null
        return GatherCache(baseBlock, null);
    }

    private static Histogram _knownStatesSize = Metrics.CreateHistogram("flatdiff_known_state_size", "timer",
        new HistogramConfiguration()
        {
            LabelNames = ["part"],
            Buckets = Histogram.LinearBuckets(0, 1, 100)
        });
    private SnapshotBundle GatherCache(StateId baseBlock, long? earliestExclusive = null)
    {
        using var _ = _repoLock.EnterScope();

        ArrayPoolList<Snapshot> knownStates = new(_inMemorySnapshotStore.KnownStatesCount / 32);

        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}. Earliest is {earliestExclusive}");

        StateId bigCacheState = _currentPersistedState;

        string exitReason = "";
        StateId current = baseBlock;
        while(TryLeaseCompactedState(current, out var entry) || TryLeaseState(current, out entry))
        {
            Snapshot state = entry;
            if (_logger.IsTrace) _logger.Trace($"Got {state.From} -> {state.To}");
            knownStates.Add(state);
            if (state.From == current) {
                exitReason = "cycle";
                break; // Some test commit two block with the same id, so we dont know the parent anymore.
            }
            current = state.From;

            if (state.To.blockNumber <= bigCacheState.blockNumber)
            {
                exitReason = $"First {state.From} to {bigCacheState}";
                break; // Or equal?
            }
            if (state.From.blockNumber <= earliestExclusive) break;
        }

        _knownStatesSize.Observe(knownStates.Count);

        // Note: By the time the previous loop finished checking all state, the big cache may have added new state and removed some
        // entry in `_inMemorySnapshotStore`. Meaning, this need to be here instead oof before the loop.
        IPersistence.IPersistenceReader bigCacheReader = LeaseReader();
        if (current != baseBlock && earliestExclusive is null && bigCacheReader.CurrentState.blockNumber != -1 && current.blockNumber > bigCacheReader.CurrentState.blockNumber)
        {
            throw new Exception($"Non consecutive snappshots. Current {current} vs {bigCacheReader.CurrentState}, {bigCacheState}, {baseBlock}, {_inMemorySnapshotStore.TryGetValue(current, out var snapshot)}, {exitReason}");
        }

        if (bigCacheReader.CurrentState.blockNumber > baseBlock.blockNumber)
        {
            _logger.Warn("Big cache too early");
            bigCacheReader.Dispose();
            bigCacheReader = new NoopPersistenceReader();
        }

        knownStates.Reverse();

        if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Earliest is {earliestExclusive}, Got {knownStates.Count} known states, {_currentPersistedState}");
        return new SnapshotBundle(knownStates, bigCacheReader, _trieNodeCache, _snapshotPool);
    }

    public bool TryLeaseCompactedState(StateId stateId, out Snapshot entry)
    {
        if (!_compactedKnownStates.TryGetValue(stateId, out entry)) return false;
        if (!entry.TryAcquire()) return false;
        return true;
    }

    public bool TryLeaseState(StateId stateId, out Snapshot entry)
    {
        if (!_inMemorySnapshotStore.TryGetValue(stateId, out entry)) return false;
        if (!entry.TryAcquire()) return false;
        return true;
    }

    public void AddSnapshot(Snapshot snapshot)
    {
        StateId startingBlock = snapshot.From;
        StateId endBlock = snapshot.To;
        using (_repoLock.EnterScope())
        {
            if (_logger.IsTrace) _logger.Trace($"Registering {startingBlock.blockNumber} to {endBlock.blockNumber}");
            if (endBlock.blockNumber <= _currentPersistedState.blockNumber)
            {
                _logger.Warn(
                    $"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.blockNumber}, bigcache number: {_currentPersistedState}");
                return;
            }

            // snapshot should have 1 lease here
            _inMemorySnapshotStore.AddBlock(endBlock, snapshot);
        }

        if (_inlineCompaction)
        {
            RunCompactJob(endBlock);
        }
        else
        {
            if (!_compactorJobs.Writer.TryWrite(endBlock))
            {
                _logger.Warn("Compactor job stall!");
                _compactorJobs.Writer.WriteAsync(endBlock).AsTask().Wait();
            }
        }
    }

    private async Task CleanIfNeeded()
    {
        await NotifyWhenSlow("add to bigcache", () => AddToBigCache());
    }

    private Histogram _addTime = Metrics.CreateHistogram("flatdiff_repo_time", "times", new HistogramConfiguration()
    {
        LabelNames = ["part"],
        Buckets = Histogram.PowersOfTenDividedBuckets(4, 12, 10)
    });

    private static Counter _flushCount = Metrics.CreateCounter("flatdiff_flush", "flush", "type");

    private void AddToBigCache()
    {
        // Attempt to add snapshots into bigcache
        while (true)
        {
            Snapshot pickedState;
            StateId? pickedSnapshot = null;
            List<StateId> toRemoveStates = new List<StateId>();
            using (_repoLock.EnterScope())
            {
                long lastSnapshotNumber = _inMemorySnapshotStore.GetLast()?.blockNumber ?? 0;
                StateId currentState = _currentPersistedState;
                if (lastSnapshotNumber - currentState.blockNumber <= (_boundary + _compactSize))
                {
                    break;
                }

                List<StateId> candidateToAdd = new List<StateId>();

                long? blockNumber = null;
                bool persistCompactedStates = false;
                //  Note: Need to verify that this is finalized
                foreach (var stateId in _inMemorySnapshotStore.GetStatesAfterBlock(currentState.blockNumber + _compactSize - 1))
                {
                    if (stateId.blockNumber > currentState.blockNumber + _compactSize)
                    {
                        break;
                    }
                    if (_compactedKnownStates.TryGetValue(stateId, out var existingState))
                    {
                        if (blockNumber is null)
                        {
                            if (existingState.From != currentState)
                            {
                                if (_logger.IsDebug) _logger.Debug($"Not using compacted state. Mismatch. {existingState.From}, query {stateId} vs {currentState}");
                                break;
                            }

                            if (_logger.IsDebug) _logger.Debug($"Setting compacted state");
                            persistCompactedStates = true;
                            blockNumber = stateId.blockNumber;
                            candidateToAdd.Add(stateId);
                        }
                        else if (blockNumber == stateId.blockNumber)
                        {
                            candidateToAdd.Add(stateId);
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Debug($"Cancelling setting compacted state, {stateId}");
                        persistCompactedStates = false;
                        candidateToAdd.Clear();
                        blockNumber = null;
                        break;
                    }
                }

                if (persistCompactedStates)
                {
                    if (_logger.IsDebug) _logger.Debug($"Using compacted state. {blockNumber}, vs {currentState}");
                }

                if (blockNumber is null)
                {
                    foreach (var stateId in _inMemorySnapshotStore.GetStatesAfterBlock(currentState.blockNumber))
                    {
                        if (blockNumber is null)
                        {
                            blockNumber = stateId.blockNumber;
                            candidateToAdd.Add(stateId);
                        }
                        else if (blockNumber == stateId.blockNumber)
                        {
                            candidateToAdd.Add(stateId);
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                Debug.Assert(candidateToAdd.Count > 0);

                if (candidateToAdd.Count > 1)
                {
                    Hash256? canonicalStateRoot = _stateRootFinder.GetCanonicalStateRootAtBlock(blockNumber.Value);
                    if (canonicalStateRoot is null)
                    {
                        _logger.Warn($"Canonical state root for block {blockNumber} not known");
                        return;
                    }

                    foreach (var stateId in candidateToAdd)
                    {
                        if (stateId.stateRoot == canonicalStateRoot)
                        {
                            pickedSnapshot = stateId;
                        }
                    }
                }
                else
                {
                    pickedSnapshot = candidateToAdd[0];
                }

                if (!pickedSnapshot.HasValue)
                {
                    // Ah, probably filter the compacted state here instead
                    _logger.Warn($"Unable to determine canonicaal snapshot");
                    return;
                }

                // Remove non-canon snapshots
                foreach (var stateId in candidateToAdd)
                {
                    if (stateId != pickedSnapshot)
                    {
                        RemoveAndReleaseCompactedKnownState(stateId);
                        RemoveAndReleaseKnownState(stateId);
                    }
                }

                if (persistCompactedStates)
                {
                    _compactedKnownStates.TryGetValue(pickedSnapshot.Value, out pickedState);
                    pickedState.AcquireLease();
                    if (_logger.IsDebug) _logger.Debug($"Picking compacted state {pickedState.From} to {pickedState.To}");

                    foreach (var stateId in _inMemorySnapshotStore.GetStatesAfterBlock(currentState.blockNumber))
                    {
                        if (stateId.blockNumber < pickedSnapshot.Value.blockNumber) toRemoveStates.Add(stateId);
                    }
                }
                else
                {
                    _inMemorySnapshotStore.TryGetValue(pickedSnapshot.Value, out pickedState);
                    pickedState.AcquireLease();
                }
            }

            // Add the canon snapshot
            long ss = Stopwatch.GetTimestamp();
            Add(pickedState);
            _addTime.WithLabels("snapshot_save").Observe(Stopwatch.GetTimestamp() - ss);

            // TODO: Determine if selfdestruct handling is required here.
            ss = Stopwatch.GetTimestamp();
            _trieNodeCache.Add(pickedState);
            _addTime.WithLabels("add_trie_cache").Observe(Stopwatch.GetTimestamp() - ss);

            // And we remove it
            using (_repoLock.EnterScope())
            {
                RemoveAndReleaseCompactedKnownState(pickedSnapshot.Value);
                RemoveAndReleaseKnownState(pickedSnapshot.Value);

                foreach (var stateId in toRemoveStates)
                {
                    RemoveAndReleaseCompactedKnownState(stateId);
                    RemoveAndReleaseKnownState(stateId);
                }
            }

            ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(pickedSnapshot.Value.blockNumber));
        }
    }

    private void RemoveAndReleaseCompactedKnownState(StateId stateId)
    {
        if (!_repoLock.IsHeldByCurrentThread) throw new Exception("Repolock must be held");
        if (_compactedKnownStates.TryGetValue(stateId, out var existingState))
        {
            _compactedKnownStates.Remove(stateId);
            existingState.Dispose();
        }
    }

    private void RemoveAndReleaseKnownState(StateId stateId)
    {
        if (!_repoLock.IsHeldByCurrentThread) throw new Exception("Repolock must be held");
        if (_inMemorySnapshotStore.TryGetValue(stateId, out var existingState))
        {
            _inMemorySnapshotStore.Remove(stateId);
            existingState.Dispose();
        }
    }

    private Counter.Child _accountWrites = _flushCount.WithLabels("account");
    private Counter.Child _storageWrites = _flushCount.WithLabels("account");
    private Counter.Child _nodesWrites = _flushCount.WithLabels("account");

    public void Add(Snapshot snapshot)
    {
        long sw = Stopwatch.GetTimestamp();
        if (snapshot.To.blockNumber - snapshot.From.blockNumber != _compactSize) _logger.Warn($"Snapshot size write is {snapshot.To.blockNumber - snapshot.From.blockNumber}");
        using (var batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To))
        {
            foreach (var toSelfDestructStorage in snapshot.SelfDestructedStorageAddresses)
            {
                if (toSelfDestructStorage.Value)
                {
                    continue;
                }
                batch.SelfDestruct(toSelfDestructStorage.Key.Value.ToAccountPath);
            }
            _addTime.WithLabels("self_destruct").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            foreach (var kv in snapshot.Accounts)
            {
                (Address addr, Account? account) = kv;
                if (account is null)
                    batch.RemoveAccount(addr);
                else
                    batch.SetAccount(addr, account);
            }
            _accountWrites.Inc(snapshot.AccountsCount);

            _addTime.WithLabels("accounts").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            foreach (var kv in snapshot.Storages)
            {
                ((Address addr, UInt256 slot), byte[] value) = kv;

                if (value is null || Bytes.AreEqual(value, StorageTree.ZeroBytes))
                {
                    batch.RemoveStorage(addr, slot);
                }
                else
                {
                    batch.SetStorage(addr, slot, value);
                }
            }
            _storageWrites.Inc(snapshot.StoragesCount);

            _addTime.WithLabels("storage").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            foreach (var tn in snapshot.TrieNodes)
            {
                (Hash256? address, TreePath path) = tn.Key;

                if (tn.Value.FullRlp.Length == 0)
                {
                    // TODO: Need to double check this case. Does it need a rewrite or not?
                    if (tn.Value.NodeType == NodeType.Unknown) continue;
                }

                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetTrieNodes(address, path, tn.Value);

                tn.Value.IsPersisted = true;
                tn.Value.PrunePersistedRecursively(1);
            }
            _nodesWrites.Inc(snapshot.TrieNodesCount);

            _addTime.WithLabels("nodes").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();
        }
        _addTime.WithLabels("dispose").Observe(Stopwatch.GetTimestamp() - sw);
        sw = Stopwatch.GetTimestamp();

        _currentPersistedState = snapshot.To;
        ClearReaderCache();
    }


    public void FlushCache(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("Flush cache not implemented");
    }

    public bool HasStateForBlock(StateId stateId)
    {
        if (_inMemorySnapshotStore.TryGetValue(stateId, out var snapshot))
        {
            return true;
        }

        if (_currentPersistedState == stateId) return true;
        return false;
    }
}
