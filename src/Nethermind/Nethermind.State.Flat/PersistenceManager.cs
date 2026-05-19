// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;

[assembly: InternalsVisibleTo("Nethermind.State.Flat.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]
[assembly: InternalsVisibleTo("Nethermind.Benchmark")]

namespace Nethermind.State.Flat;

public class PersistenceManager(
    IFlatDbConfig configuration,
    IFinalizedStateProvider finalizedStateProvider,
    IPersistence persistence,
    ISnapshotRepository snapshotRepository,
    ILogManager logManager,
    PersistedSnapshotCompactors persistedSnapshotCompactors,
    PersistedSnapshotRepositories persistedSnapshotRepositories) : IPersistenceManager
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistenceManager>();
    private readonly int _minReorgDepth = configuration.MinReorgDepth;
    private readonly int _maxInMemoryBaseSnapshotCount = configuration.MaxInMemoryBaseSnapshotCount;
    private readonly int _compactSize = configuration.CompactSize;
    private readonly bool _enableLongFinality = configuration.EnableLongFinality;
    private readonly IPersistence _persistence = persistence;
    private readonly ISnapshotRepository _snapshotRepository = snapshotRepository;
    private readonly IFinalizedStateProvider _finalizedStateProvider = finalizedStateProvider;
    private readonly IPersistedSnapshotCompactor _smallCompactor = persistedSnapshotCompactors.Small;
    private readonly IPersistedSnapshotCompactor _largeCompactor = persistedSnapshotCompactors.Large;
    private readonly IPersistedSnapshotRepository _smallRepo = persistedSnapshotRepositories.Small;
    private readonly IPersistedSnapshotRepository _largeRepo = persistedSnapshotRepositories.Large;
    private readonly List<(Hash256, TreePath)> _trieNodesSortBuffer = []; // Presort make it faster
    private readonly Lock _persistenceLock = new();

    private readonly Channel<ArrayPoolList<StateId>> _compactPersistedJobs = Channel.CreateBounded<ArrayPoolList<StateId>>(16);
    private readonly Channel<StateId> _boundaryCompactJobs = Channel.CreateBounded<StateId>(16);
    private readonly CancellationTokenSource _cancelTokenSource = new();
    private Task? _compactPersistedTask;
    private Task[]? _boundaryCompactorTasks;

    private const int BoundaryCompactorWorkerCount = 4;

    private StateId _currentPersistedStateId = StateId.PreGenesis;

    private Task EnsureCompactorStarted()
    {
        _compactPersistedTask ??= RunPersistedCompactor(_cancelTokenSource.Token);
        if (_boundaryCompactorTasks is null)
        {
            Task[] tasks = new Task[BoundaryCompactorWorkerCount];
            for (int i = 0; i < BoundaryCompactorWorkerCount; i++)
                tasks[i] = RunBoundaryCompactor(_cancelTokenSource.Token);
            _boundaryCompactorTasks = tasks;
        }
        return _compactPersistedTask;
    }

    private readonly Histogram _persistedSnapshotConvertTime =
        Prometheus.Metrics.CreateHistogram("persisted_snapshot_convert_time", "persisted_snapshot_convert_time", "size");

    private async Task RunPersistedCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ArrayPoolList<StateId> batch in _compactPersistedJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ProcessCompactBatch(batch);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error compacting persisted snapshot batch. {ex}");
                }
                finally
                {
                    batch.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            while (_compactPersistedJobs.Reader.TryRead(out ArrayPoolList<StateId>? batch))
                batch.Dispose();
        }
    }

    private async Task ProcessCompactBatch(ArrayPoolList<StateId> batch)
    {
        if (batch.Count == 0) return;

        // Offload boundary states (block divisible by _compactSize — heaviest merges) to the
        // parallel boundary channel so the next batch can start before these compactions finish.
        using ArrayPoolList<StateId> boundaries = new(batch.Count);
        SortedDictionary<int, List<StateId>> buckets = new();
        for (int i = 0; i < batch.Count; i++)
        {
            StateId s = batch[i];
            long b = s.BlockNumber;
            if (b == 0) continue;

            if (b % _compactSize == 0)
            {
                boundaries.Add(s);
                continue;
            }

            // Non-boundary: lowest-set-bit alignment is strictly < _compactSize.
            int compactSize = (int)(b & -b);
            if (!buckets.TryGetValue(compactSize, out List<StateId>? bucket))
                buckets[compactSize] = bucket = [];
            bucket.Add(s);
        }

        // Non-boundary states live only in the small repo (see AddToPersistence:
        // _smallRepo.ConvertSnapshotToPersistedSnapshot for non-boundary blocks).
        foreach (KeyValuePair<int, List<StateId>> kv in buckets)
            Parallel.ForEach(kv.Value, state => _smallCompactor.DoCompactSnapshot(state));

        foreach (StateId boundary in boundaries)
            await _boundaryCompactJobs.Writer.WriteAsync(boundary, _cancelTokenSource.Token);
    }

    private async Task RunBoundaryCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateId state in _boundaryCompactJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // Boundary snapshots always live in the large repo (see AddToPersistence:
                    // _largeRepo.ConvertSnapshotToPersistedSnapshot at the boundary block).
                    _largeCompactor.DoCompactSnapshot(state);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error compacting boundary persisted snapshot {state}. {ex}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancelTokenSource.Cancel();
        _compactPersistedJobs.Writer.Complete();
        _boundaryCompactJobs.Writer.Complete();
        if (_compactPersistedTask is not null)
            await _compactPersistedTask;
        if (_boundaryCompactorTasks is not null)
            await Task.WhenAll(_boundaryCompactorTasks);
        _cancelTokenSource.Dispose();
    }

    public IPersistence.IPersistenceReader LeaseReader() => _persistence.CreateReader();

    public StateId GetCurrentPersistedStateId()
    {
        if (_currentPersistedStateId == StateId.PreGenesis)
        {
            using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
            _currentPersistedStateId = reader.CurrentState;
        }
        return _currentPersistedStateId;
    }

    /// <summary>
    /// Two-phase action: Phase 1 (persistence to RocksDB) runs first; Phase 2 (conversion to
    /// the HSST persisted-snapshot tier) runs only when Phase 1 returns no candidate.
    /// </summary>
    /// <remarks>
    /// Phase 1 seed selection:
    /// <list type="bullet">
    ///   <item>Force-persist short-circuit when <c>snapshotsDepth &gt; MaxInMemoryBaseSnapshotCount</c> →
    ///   seed = <see cref="ISnapshotRepository.LastRegisteredState"/>; the finality gate is bypassed.</item>
    ///   <item>Otherwise, require <c>finalizedBlock &gt; persistedBlock + CompactSize</c> AND
    ///   <c>snapshotsDepth + CompactSize &gt; MinReorgDepth</c> → seed = finalized state.</item>
    /// </list>
    /// Phase 2 runs only with <see cref="_enableLongFinality"/> enabled AND
    /// <c>SnapshotCount &gt; MaxInMemoryBaseSnapshotCount</c>.
    /// </remarks>
    internal (PersistedSnapshot? ToPersistPersistedSnapshot, Snapshot? ToPersist, ConversionCandidate? ToConvert) DetermineSnapshotAction(StateId latestSnapshot)
    {
        StateId currentPersistedState = GetCurrentPersistedStateId();
        long snapshotsDepth = latestSnapshot.BlockNumber - currentPersistedState.BlockNumber;

        // ---- Phase 1: persistence to RocksDB ----
        // Up to two seeds populate the BFS queue: the finalized state (preferred — anchors the
        // canonical chain) and the in-memory tip (`LastRegisteredState`, force-persist fallback).
        // The force-persist trigger uses tip-only; the normal trigger uses finalized + tip so the
        // walk still has an entry point when the snapshot graph hasn't filled in between persisted
        // and finalized yet.
        StateId? finalizedSeed = null;
        StateId? tipSeed = null;
        if (snapshotsDepth > _maxInMemoryBaseSnapshotCount)
        {
            tipSeed = _snapshotRepository.LastRegisteredState;
        }
        else
        {
            long finalizedBlockNumber = _finalizedStateProvider.FinalizedBlockNumber;
            if (finalizedBlockNumber >= currentPersistedState.BlockNumber + _compactSize
                && snapshotsDepth + _compactSize > _minReorgDepth)
            {
                Hash256? finalizedStateRoot = _finalizedStateProvider.GetFinalizedStateRootAt(finalizedBlockNumber);
                if (finalizedStateRoot is not null)
                    finalizedSeed = new StateId(finalizedBlockNumber, finalizedStateRoot);
                tipSeed = _snapshotRepository.LastRegisteredState;
            }
        }

        if (finalizedSeed is not null || tipSeed is not null)
        {
            (PersistedSnapshot? persisted, Snapshot? inMemory) =
                TryFindSnapshotToPersist(finalizedSeed, tipSeed, currentPersistedState);
            if (persisted is not null || inMemory is not null)
                return (persisted, inMemory, null);
        }

        // ---- Phase 2: conversion to the persisted-snapshot tier ----
        if (!_enableLongFinality) return (null, null, null);
        if (_snapshotRepository.SnapshotCount <= _maxInMemoryBaseSnapshotCount) return (null, null, null);

        return (null, null, TryFindSnapshotToConvert(currentPersistedState));
    }

    /// <summary>
    /// Phase 1 BFS — walks backward over the snapshot graph from <paramref name="seed"/> via
    /// <see cref="Snapshot.From"/> pointers, returning the first snapshot whose <c>From</c> equals
    /// <paramref name="currentPersistedState"/>. At each visited <c>StateId</c> the four candidate
    /// sources are tried in this fixed priority order:
    /// <list type="number">
    ///   <item><c>_largeRepo.TryLeaseSnapshotTo</c> — persisted base, depth == CompactSize</item>
    ///   <item><c>_smallRepo.TryLeaseSnapshotTo</c> — persisted base, sub-CompactSize</item>
    ///   <item><c>_snapshotRepository.TryLeaseCompactedState</c> filtered to depth == CompactSize —
    ///   in-memory boundary compacted</item>
    ///   <item><c>_snapshotRepository.TryLeaseState</c> — in-memory base, depth == 1</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Compacted persisted entries (large hierarchical / small compacted) and non-boundary
    /// in-memory compacted entries are not returnable candidates; they are still traversed for
    /// navigation, acting as skip pointers that jump multiple blocks per hop and shorten the path
    /// to a candidate.
    /// </remarks>
    private (PersistedSnapshot? Persisted, Snapshot? InMemory) TryFindSnapshotToPersist(
        StateId? finalizedSeed, StateId? tipSeed, StateId currentPersistedState)
    {
        HashSet<StateId> visited = [];
        Queue<StateId> queue = new();
        EnqueueAncestor(finalizedSeed, currentPersistedState, visited, queue);
        EnqueueAncestor(tipSeed, currentPersistedState, visited, queue);
        if (queue.Count == 0) return (null, null);

        while (queue.TryDequeue(out StateId current))
        {
            // Priority 1: persisted base in the Large tier (depth == CompactSize).
            if (_largeRepo.TryLeaseSnapshotTo(current, out PersistedSnapshot? largeBase))
            {
                if (largeBase!.From == currentPersistedState) return (largeBase, null);
                EnqueueAncestor(largeBase.From, currentPersistedState, visited, queue);
                largeBase.Dispose();
            }

            // Priority 2: persisted base in the Small tier (sub-CompactSize).
            if (_smallRepo.TryLeaseSnapshotTo(current, out PersistedSnapshot? smallBase))
            {
                if (smallBase!.From == currentPersistedState) return (smallBase, null);
                EnqueueAncestor(smallBase.From, currentPersistedState, visited, queue);
                smallBase.Dispose();
            }

            // Priority 3: in-memory boundary compacted (depth == CompactSize).
            if (_snapshotRepository.TryLeaseCompactedState(current, out Snapshot? inMemCompacted))
            {
                if (inMemCompacted!.To.BlockNumber - inMemCompacted.From.BlockNumber == _compactSize
                    && inMemCompacted.From == currentPersistedState)
                    return (null, inMemCompacted);
                EnqueueAncestor(inMemCompacted.From, currentPersistedState, visited, queue);
                inMemCompacted.Dispose();
            }

            // Priority 4: in-memory base (depth == 1).
            if (_snapshotRepository.TryLeaseState(current, out Snapshot? inMemBase))
            {
                if (inMemBase!.From == currentPersistedState) return (null, inMemBase);
                EnqueueAncestor(inMemBase.From, currentPersistedState, visited, queue);
                inMemBase.Dispose();
            }

            // Pure navigation: compacted persisted entries are never returned as candidates but
            // act as skip pointers (their range covers multiple blocks per hop).
            if (_largeRepo.TryLeaseCompactedSnapshotTo(current, out PersistedSnapshot? largeCompacted))
            {
                EnqueueAncestor(largeCompacted!.From, currentPersistedState, visited, queue);
                largeCompacted.Dispose();
            }
            if (_smallRepo.TryLeaseCompactedSnapshotTo(current, out PersistedSnapshot? smallCompacted))
            {
                EnqueueAncestor(smallCompacted!.From, currentPersistedState, visited, queue);
                smallCompacted.Dispose();
            }
        }

        return (null, null);
    }

    private static void EnqueueAncestor(StateId? from, in StateId currentPersistedState, HashSet<StateId> visited, Queue<StateId> queue)
    {
        if (from is not null && from.Value.BlockNumber > currentPersistedState.BlockNumber && visited.Add(from.Value))
            queue.Enqueue(from.Value);
    }

    /// <summary>
    /// Phase 2 — scan in-memory snapshots in ascending block-number order, picking the first whose
    /// <c>From</c> is already on disk (either equals <paramref name="currentPersistedState"/> or is the
    /// <c>To</c> of an existing persisted snapshot in either tier). Priority within each <c>StateId</c>:
    /// boundary-CompactSize compacted (triggers batch convert) over base (single convert).
    /// </summary>
    private ConversionCandidate? TryFindSnapshotToConvert(StateId currentPersistedState)
    {
        using ArrayPoolList<StateId> ordered = _snapshotRepository.GetSnapshotBeforeStateId(long.MaxValue);
        foreach (StateId X in ordered)
        {
            // Priority 1: boundary-CompactSize in-memory compacted → batch convert.
            if (_snapshotRepository.TryLeaseCompactedState(X, out Snapshot? compacted))
            {
                if (compacted!.To.BlockNumber - compacted.From.BlockNumber == _compactSize
                    && IsOnDisk(compacted.From, currentPersistedState))
                    return new ConversionCandidate(compacted, Base: null);
                compacted.Dispose();
            }

            // Priority 2: in-memory base → single convert.
            if (_snapshotRepository.TryLeaseState(X, out Snapshot? baseSnap))
            {
                if (IsOnDisk(baseSnap!.From, currentPersistedState))
                    return new ConversionCandidate(Compacted: null, baseSnap);
                baseSnap.Dispose();
            }
        }

        return null;
    }

    private bool IsOnDisk(in StateId state, in StateId currentPersistedState) =>
        state == currentPersistedState
        || _largeRepo.HasBaseSnapshot(state)
        || _smallRepo.HasBaseSnapshot(state);

    internal sealed record ConversionCandidate(Snapshot? Compacted, Snapshot? Base);

    public void AddToPersistence(StateId latestSnapshot)
    {
        using Lock.Scope scope = _persistenceLock.EnterScope();
        while (true)
        {
            (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, ConversionCandidate? toConvert) =
                DetermineSnapshotAction(latestSnapshot);

            if (toPersist is not null)
            {
                using Snapshot _ = toPersist;
                PersistSnapshot(toPersist);
                _currentPersistedStateId = toPersist.To;
            }
            else if (persistedToPersist is not null)
            {
                using PersistedSnapshot _ = persistedToPersist;
                PersistPersistedSnapshot(persistedToPersist);
                _currentPersistedStateId = persistedToPersist.To;
                int pruned = _smallRepo.PruneBefore(persistedToPersist.To) + _largeRepo.PruneBefore(persistedToPersist.To);
                if (pruned > 0)
                {
                    Metrics.PersistedSnapshotPrunes += pruned;
                    Metrics.PersistedSnapshotCount = _smallRepo.SnapshotCount + _largeRepo.SnapshotCount;
                    Metrics.PersistedSnapshotMemory = _smallRepo.BaseSnapshotMemory + _largeRepo.BaseSnapshotMemory;
                    Metrics.CompactedPersistedSnapshotMemory = _smallRepo.CompactedSnapshotMemory + _largeRepo.CompactedSnapshotMemory;
                    if (_logger.IsDebug) _logger.Debug($"Pruned {pruned} persisted snapshots before block {persistedToPersist.To.BlockNumber}");
                }
            }
            else if (toConvert is not null)
            {
                DoConvert(toConvert);
            }
            else
            {
                break;
            }
        }
    }

    private void DoConvert(ConversionCandidate candidate)
    {
        if (candidate.Compacted is not null)
        {
            // Branch A — boundary CompactSize compacted: batch-convert every in-memory entry in
            // the range it spans, then promote the compacted itself.
            Snapshot compacted = candidate.Compacted;
            try
            {
                long start = compacted.From.BlockNumber + 1;
                long end = compacted.To.BlockNumber;

                ArrayPoolList<StateId> allStateIds = new(64);
                for (long b = start; b <= end; b++)
                {
                    using ArrayPoolList<StateId> statesAtBlock = _snapshotRepository.GetStatesAtBlockNumber(b);
                    foreach (StateId state in statesAtBlock)
                        allStateIds.Add(state);
                }

                Parallel.ForEach(
                    allStateIds,
                    new ParallelOptions { CancellationToken = _cancelTokenSource.Token },
                    state =>
                    {
                        if (_snapshotRepository.TryLeaseState(state, out Snapshot? snap))
                        {
                            long sw = Stopwatch.GetTimestamp();
                            _smallRepo.ConvertSnapshotToPersistedSnapshot(snap);
                            _persistedSnapshotConvertTime.WithLabels("base").Observe(Stopwatch.GetTimestamp() - sw);
                            snap.Dispose();
                        }
                    });

                long sw2 = Stopwatch.GetTimestamp();
                _largeRepo.ConvertSnapshotToPersistedSnapshot(compacted);
                _persistedSnapshotConvertTime.WithLabels("full32").Observe(Stopwatch.GetTimestamp() - sw2);

                EnsureCompactorStarted();
                _compactPersistedJobs.Writer.WriteAsync(allStateIds).AsTask().Wait();

                _snapshotRepository.RemoveStatesUntil(end);
            }
            finally
            {
                compacted.Dispose();
            }
        }
        else
        {
            // Branch B — single base convert (fragmented case: no full-CompactSize compacted
            // available for the candidate range yet).
            Snapshot baseSnap = candidate.Base!;
            try
            {
                long sw = Stopwatch.GetTimestamp();
                _smallRepo.ConvertSnapshotToPersistedSnapshot(baseSnap);
                _persistedSnapshotConvertTime.WithLabels("base").Observe(Stopwatch.GetTimestamp() - sw);

                EnsureCompactorStarted();
                ArrayPoolList<StateId> single = new(1) { baseSnap.To };
                _compactPersistedJobs.Writer.WriteAsync(single).AsTask().Wait();

                _snapshotRepository.RemoveAndReleaseKnownState(baseSnap.To);
            }
            finally
            {
                baseSnap.Dispose();
            }
        }
    }

    /// <summary>
    /// Force persist all snapshots regardless of finalization status.
    /// Used by FlushCache to ensure all state is persisted before clearing caches.
    /// </summary>
    public StateId FlushToPersistence()
    {
        using Lock.Scope scope = _persistenceLock.EnterScope();

        StateId currentPersistedState = GetCurrentPersistedStateId();
        StateId? latestStateId = _snapshotRepository.GetLastSnapshotId();

        if (latestStateId is null)
        {
            return currentPersistedState;
        }

        // Persist all snapshots from current persisted state to latest. Flush ignores finality
        // entirely — seed the BFS with the in-memory tip so every hop on the chain (finalized or
        // not) is reachable.
        while (currentPersistedState.BlockNumber < latestStateId.Value.BlockNumber)
        {
            StateId? tipSeed = _snapshotRepository.LastRegisteredState;
            StateId? finalizedSeed = null;
            long finalizedBlockNumber = _finalizedStateProvider.FinalizedBlockNumber;
            if (finalizedBlockNumber > currentPersistedState.BlockNumber)
            {
                Hash256? finalizedStateRoot = _finalizedStateProvider.GetFinalizedStateRootAt(finalizedBlockNumber);
                if (finalizedStateRoot is not null)
                    finalizedSeed = new StateId(finalizedBlockNumber, finalizedStateRoot);
            }

            if (tipSeed is null && finalizedSeed is null) break;

            (PersistedSnapshot? persisted, Snapshot? snapshotToPersist) =
                TryFindSnapshotToPersist(finalizedSeed, tipSeed, currentPersistedState);

            if (persisted is not null)
            {
                using PersistedSnapshot persistedScope = persisted;
                PersistPersistedSnapshot(persisted);
                _currentPersistedStateId = persisted.To;
                currentPersistedState = _currentPersistedStateId;
                continue;
            }

            if (snapshotToPersist is null) break;

            using Snapshot inMemScope = snapshotToPersist;
            PersistSnapshot(snapshotToPersist);
            _currentPersistedStateId = snapshotToPersist.To;
            currentPersistedState = _currentPersistedStateId;
        }

        return currentPersistedState;
    }

    public void ResetPersistedStateId()
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        _currentPersistedStateId = reader.CurrentState;
    }

    internal void PersistSnapshot(Snapshot snapshot)
    {
        long compactLength = snapshot.To.BlockNumber! - snapshot.From.BlockNumber!;

        // Usually at the start of the application
        if (compactLength != _compactSize && _logger.IsTrace) _logger.Trace($"Persisting non compacted state of length {compactLength}");

        long sw = Stopwatch.GetTimestamp();
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To))
        {
            foreach (KeyValuePair<HashedKey<Address>, bool> toSelfDestructStorage in snapshot.SelfDestructedStorageAddresses)
            {
                if (toSelfDestructStorage.Value)
                {
                    continue;
                }

                batch.SelfDestruct(toSelfDestructStorage.Key.Key);
            }

            foreach (KeyValuePair<HashedKey<Address>, Account?> kv in snapshot.Accounts)
            {
                batch.SetAccount(kv.Key.Key, kv.Value);
            }

            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kv in snapshot.Storages)
            {
                (Address addr, UInt256 slot) = kv.Key.Key;

                batch.SetStorage(addr, slot, kv.Value);
            }

            _trieNodesSortBuffer.Clear();
            foreach (TreePath path in snapshot.StateNodeKeys)
            {
                _trieNodesSortBuffer.Add((Hash256.Zero, path)); // Hash256.Zero is a placeholder; state node keys don't have an address component
            }
            _trieNodesSortBuffer.Sort();

            long stateNodesSize = 0;
            foreach ((Hash256, TreePath) k in _trieNodesSortBuffer)
            {
                (_, TreePath path) = k;

                snapshot.TryGetStateNode(new HashedKey<TreePath>(path), out TrieNode? node);

                if (node!.FullRlp.Length == 0)
                {
                    // TODO: Need to double check this case. Does it need a rewrite or not?
                    if (node.NodeType == NodeType.Unknown)
                    {
                        continue;
                    }
                }

                ReadOnlySpan<byte> rlp = node.FullRlp.AsSpan();
                stateNodesSize += rlp.Length;
                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetStateTrieNode(path, rlp);

                node.IsPersisted = true;
            }

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StorageTrieNodeKeys);
            _trieNodesSortBuffer.Sort();

            long storageNodesSize = 0;
            foreach ((Hash256, TreePath) k in _trieNodesSortBuffer)
            {
                (Hash256 address, TreePath path) = k;

                snapshot.TryGetStorageNode(new HashedKey<(Hash256, TreePath)>((address, path)), out TrieNode? node);

                if (node!.FullRlp.Length == 0)
                {
                    // TODO: Need to double check this case. Does it need a rewrite or not?
                    if (node.NodeType == NodeType.Unknown)
                    {
                        continue;
                    }
                }

                ReadOnlySpan<byte> rlp = node.FullRlp.AsSpan();
                storageNodesSize += rlp.Length;
                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetStorageTrieNode(address, path, rlp);
                node.IsPersisted = true;
            }

            Metrics.FlatPersistenceSnapshotSize.Observe(stateNodesSize, labels: new StringLabel("state_nodes"));
            Metrics.FlatPersistenceSnapshotSize.Observe(storageNodesSize, labels: new StringLabel("storage_nodes"));
        }

        Metrics.FlatPersistenceTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

    internal void PersistPersistedSnapshot(PersistedSnapshot snapshot)
    {
        long sw = Stopwatch.GetTimestamp();

        using WholeReadSession session = snapshot.BeginWholeReadSession();
        PersistedSnapshotScanner scanner = new(session, snapshot);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To))
        {
            // Single walk over column 0x01: SD, account, and slot sub-tags all sit in the
            // same per-address inner HSST, so one outer pass + TryResolveAll resolves all
            // three for each address. Per-address ordering (SD before SetAccount/SetStorage)
            // is preserved within the row; cross-address ordering is irrelevant to the
            // write batch.
            foreach (PersistedSnapshotScanner.PerAddressEntry entry in scanner.PerAddresses)
            {
                if (entry.SelfDestructFlag is false)
                    batch.SelfDestruct(entry.Address);

                if (entry.HasAccount)
                    batch.SetAccount(entry.Address, entry.Account);

                foreach (PersistedSnapshotScanner.SlotEntry slot in entry.Slots)
                    batch.SetStorage(entry.Address, slot.Slot, slot.Value);
            }

            foreach (PersistedSnapshotScanner.StateNodeEntry entry in scanner.StateNodes)
                batch.SetStateTrieNode(entry.Path, entry.Rlp);

            foreach (PersistedSnapshotScanner.StorageNodeEntry entry in scanner.StorageNodes)
                batch.SetStorageTrieNode(entry.AddressHash.ToCommitment(), entry.Path, entry.Rlp);
        }

        Metrics.FlatPersistenceTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

}
