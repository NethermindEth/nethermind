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
    IPersistedSnapshotCompactor persistedSnapshotCompactor,
    IPersistedSnapshotRepository persistedSnapshotRepository) : IPersistenceManager
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistenceManager>();
    private readonly int _minReorgDepth = configuration.MinReorgDepth;
    private readonly int _maxInMemoryBaseSnapshotCount = configuration.MaxInMemoryBaseSnapshotCount;
    private readonly int _longFinalityReorgDepth = configuration.LongFinalityReorgDepth;
    private readonly int _compactSize = configuration.CompactSize;
    private readonly bool _enableLongFinality = configuration.EnableLongFinality;
    private readonly IPersistence _persistence = persistence;
    private readonly ISnapshotRepository _snapshotRepository = snapshotRepository;
    private readonly IFinalizedStateProvider _finalizedStateProvider = finalizedStateProvider;
    private readonly IPersistedSnapshotCompactor _compactor = persistedSnapshotCompactor;
    private readonly IPersistedSnapshotRepository _repo = persistedSnapshotRepository;
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

        using ArrayPoolList<StateId> boundaries = new(batch.Count);
        SortedDictionary<int, List<StateId>> buckets = new();
        for (int i = 0; i < batch.Count; i++)
        {
            StateId s = batch[i];
            long b = s.BlockNumber;
            if (b == 0) continue;

            if (b % _compactSize == 0)
            {
                // A CompactSize boundary — its persistable is produced below via
                // DoCompactPersistable, so it is not bucketed for DoCompactSnapshot.
                boundaries.Add(s);
                continue;
            }

            // Non-boundary: bucket by power-of-2 alignment (always < CompactSize).
            int compactSize = (int)(b & -b);
            if (!buckets.TryGetValue(compactSize, out List<StateId>? bucket))
                buckets[compactSize] = bucket = [];
            bucket.Add(s);
        }

        // Ascending bucket order: each sub-CompactSize layer's inputs (the previous layer's
        // outputs) exist before it runs.
        foreach (KeyValuePair<int, List<StateId>> kv in buckets)
            Parallel.ForEach(kv.Value, state => _compactor.DoCompactSnapshot(state));

        // The sub-CompactSize layers are in place — produce each boundary's persistable.
        foreach (StateId boundary in boundaries)
            _compactor.DoCompactPersistable(boundary);

        // Hand a boundary to the boundary compactor only when its highest power of two
        // exceeds CompactSize — i.e. it has a >CompactSize hierarchical-merge window. One
        // whose highest power of two is exactly CompactSize would just no-op there.
        foreach (StateId boundary in boundaries)
        {
            long b = boundary.BlockNumber;
            if ((b & -b) > _compactSize)
                await _boundaryCompactJobs.Writer.WriteAsync(boundary, _cancelTokenSource.Token);
        }
    }

    private async Task RunBoundaryCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateId state in _boundaryCompactJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // The persistable for this boundary was already produced in
                    // ProcessCompactBatch; DoCompactSnapshot here only does the
                    // >CompactSize hierarchical merges.
                    _compactor.DoCompactSnapshot(state);
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
    /// Phase 1 single-seed selection:
    /// <list type="bullet">
    ///   <item>If <c>finalizedBlock &gt;= persistedBlock + CompactSize</c> AND
    ///   <c>snapshotsDepth + CompactSize &gt; MinReorgDepth</c> → seed = canonical state at
    ///   the next boundary block (<c>persistedBlock + CompactSize</c>). Looked up via
    ///   <see cref="IFinalizedStateProvider"/> — the boundary is always locally synced even
    ///   during catch-up sync where the CL-reported finalized tip is beyond the chain head.</item>
    ///   <item>Else if <c>snapshotsDepth &gt; LongFinalityReorgDepth</c> (backstop, finalization
    ///   stalled) → seed = latest persisted-snapshot tier state.</item>
    ///   <item>Else → no seed; Phase 1 doesn't run, fall through to Phase 2.</item>
    /// </list>
    /// Phase 2 runs only with <see cref="_enableLongFinality"/> enabled AND
    /// <c>SnapshotCount &gt; MaxInMemoryBaseSnapshotCount</c>.
    /// </remarks>
    internal (PersistedSnapshot? ToPersistPersistedSnapshot, Snapshot? ToPersist, ConversionCandidate? ToConvert) DetermineSnapshotAction(StateId latestSnapshot)
    {
        StateId currentPersistedState = GetCurrentPersistedStateId();
        long snapshotsDepth = latestSnapshot.BlockNumber - currentPersistedState.BlockNumber;

        // ---- Phase 1: persistence to RocksDB ----
        // Single seed. Two sources, in priority order: the canonical state at the next
        // boundary block (normal — anchors the canonical chain at a locally-synced block,
        // robust to catch-up sync where the CL-reported finalized tip is beyond chain head),
        // or the latest persisted-snapshot tier state (backstop, only when in-memory has
        // grown past LongFinalityReorgDepth). The backstop seed is always on disk, so the
        // BFS is rooted on an in-graph node by construction.
        StateId? seed = null;
        long finalizedBlockNumber = _finalizedStateProvider.FinalizedBlockNumber;
        if (finalizedBlockNumber >= currentPersistedState.BlockNumber + _compactSize
            && snapshotsDepth + _compactSize > _minReorgDepth)
        {
            // Anchor at the next boundary block, not at the CL-reported finalized tip. The
            // outer gate guarantees boundary <= finalizedBlockNumber, so the provider's own
            // range check passes; the boundary is below chain head by construction, so the
            // canonical header is in the block tree and FindHeader resolves.
            long targetBlockNumber = currentPersistedState.BlockNumber + _compactSize;
            Hash256? canonicalRoot = _finalizedStateProvider.GetFinalizedStateRootAt(targetBlockNumber);
            if (canonicalRoot is not null)
                seed = new StateId(targetBlockNumber, canonicalRoot);
        }
        else if (snapshotsDepth > _longFinalityReorgDepth)
        {
            seed = _repo.LastRegisteredState;
        }

        if (seed is not null)
        {
            (PersistedSnapshot? persisted, Snapshot? inMemory) =
                TryFindSnapshotToPersist(seed.Value, currentPersistedState);
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
    ///   <item><c>_repo.TryLeasePersistableCompactedSnapshotTo</c> — the CompactSize-wide
    ///   persistable (one persist covers the whole window)</item>
    ///   <item><c>_repo.TryLeaseSnapshotTo</c> — a persisted base (fallback when the
    ///   persistable for this window has not been compacted yet)</item>
    ///   <item><c>_snapshotRepository.TryLeaseCompactedState</c> filtered to depth == CompactSize —
    ///   in-memory boundary compacted</item>
    ///   <item><c>_snapshotRepository.TryLeaseState</c> — in-memory base, depth == 1</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// &gt;CompactSize compacted persisted entries and non-boundary in-memory compacted entries
    /// are not returnable candidates; they are still traversed for navigation, acting as skip
    /// pointers that jump multiple blocks per hop and shorten the path to a candidate.
    /// </remarks>
    private (PersistedSnapshot? Persisted, Snapshot? InMemory) TryFindSnapshotToPersist(
        StateId seed, StateId currentPersistedState)
    {
        if (seed.BlockNumber <= currentPersistedState.BlockNumber) return (null, null);

        HashSet<StateId> visited = [seed];
        Queue<StateId> queue = new();
        queue.Enqueue(seed);

        while (queue.TryDequeue(out StateId current))
        {
            // Priority 1: the CompactSize-wide persistable — the fast path, one persist
            // covers a whole CompactSize window.
            if (_repo.TryLeasePersistableCompactedSnapshotTo(current, out PersistedSnapshot? persistable))
            {
                if (persistable!.From == currentPersistedState) return (persistable, null);
                EnqueueAncestor(persistable.From, currentPersistedState, visited, queue);
                persistable.Dispose();
            }

            // Priority 2: a persisted base — the fallback when the persistable for this
            // window has not been produced by the batched compactor yet.
            if (_repo.TryLeaseSnapshotTo(current, out PersistedSnapshot? persistedBase))
            {
                if (persistedBase!.From == currentPersistedState) return (persistedBase, null);
                EnqueueAncestor(persistedBase.From, currentPersistedState, visited, queue);
                persistedBase.Dispose();
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

            // Pure navigation: >CompactSize compacted entries are never returned as candidates
            // but act as skip pointers (their range covers multiple blocks per hop).
            if (_repo.TryLeaseCompactedSnapshotTo(current, out PersistedSnapshot? compacted))
            {
                EnqueueAncestor(compacted!.From, currentPersistedState, visited, queue);
                compacted.Dispose();
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
    /// Phase 2 — scan in-memory snapshots in ascending block-number order using two passes so
    /// boundary-CompactSize compacted candidates (Branch A) globally win over base candidates
    /// (Branch B), regardless of block-number ordering. Boundary compacted exist only at
    /// multiples of <see cref="_compactSize"/> while bases exist at every block, so a
    /// single-pass ascending walk would always pick the smallest-block base first and starve
    /// the boundary candidates.
    /// </summary>
    /// <remarks>
    /// Both passes share the same <c>ordered</c> list and the same on-disk gate
    /// (<see cref="IsOnDisk"/> — either equals <paramref name="currentPersistedState"/> or is
    /// the <c>To</c> of an existing persisted base snapshot). Pass 1 keeps the
    /// <c>span == _compactSize</c> guard so sub-CompactSize compacted (width 1/2/4/8/16,
    /// produced by <see cref="SnapshotCompactor"/> at non-boundary blocks) cannot be
    /// returned as boundary candidates.
    /// </remarks>
    private ConversionCandidate? TryFindSnapshotToConvert(StateId currentPersistedState)
    {
        using ArrayPoolList<StateId> ordered = _snapshotRepository.GetSnapshotBeforeStateId(long.MaxValue);

        // Pass 1 (global): boundary-CompactSize in-memory compacted → Branch A.
        foreach (StateId X in ordered)
        {
            if (!_snapshotRepository.TryLeaseCompactedState(X, out Snapshot? compacted)) continue;

            if (compacted!.To.BlockNumber - compacted.From.BlockNumber == _compactSize
                && IsOnDisk(compacted.From, currentPersistedState))
            {
                return new ConversionCandidate(compacted, Base: null);
            }
            compacted.Dispose();
        }

        // Pass 2 (fallback): in-memory base → Branch B.
        foreach (StateId X in ordered)
        {
            if (!_snapshotRepository.TryLeaseState(X, out Snapshot? baseSnap)) continue;

            if (IsOnDisk(baseSnap!.From, currentPersistedState))
            {
                return new ConversionCandidate(Compacted: null, baseSnap);
            }
            baseSnap.Dispose();
        }

        return null;
    }

    private bool IsOnDisk(in StateId state, in StateId currentPersistedState) =>
        state == currentPersistedState || _repo.HasBaseSnapshot(state);

    internal sealed record ConversionCandidate(Snapshot? Compacted, Snapshot? Base);

    public void AddToPersistence(StateId latestSnapshot)
    {
        using Lock.Scope scope = _persistenceLock.EnterScope();
        // Bound the drain per invocation so a deep backlog (e.g. early catch-up sync) does
        // not block the processing thread for an unbounded time. The caller re-enters on
        // every block, so the remaining backlog is consumed across subsequent invocations.
        const int MaxDrainIterations = 4;
        for (int i = 0; i < MaxDrainIterations; i++)
        {
            (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, ConversionCandidate? toConvert) =
                DetermineSnapshotAction(latestSnapshot);

            if (toPersist is not null)
            {
                using Snapshot _ = toPersist;
                PersistSnapshot(toPersist);
                _currentPersistedStateId = toPersist.To;
                PrunePersistedTierBefore(toPersist.To);
            }
            else if (persistedToPersist is not null)
            {
                using PersistedSnapshot _ = persistedToPersist;
                PersistPersistedSnapshot(persistedToPersist);
                _currentPersistedStateId = persistedToPersist.To;
                PrunePersistedTierBefore(persistedToPersist.To);
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

    /// <summary>
    /// Drop persisted-snapshot tier entries whose <c>To.BlockNumber &lt; newPersisted.BlockNumber</c>.
    /// Called after every successful RocksDB persist (in-memory or tier source) so the tier
    /// doesn't accumulate entries that RocksDB has already superseded.
    /// </summary>
    /// <remarks>
    /// The per-removal metric updates (count / memory / prunes) happen delta-wise inside the
    /// repo's <c>PruneBefore</c>, so no metric recompute is needed here.
    /// </remarks>
    private void PrunePersistedTierBefore(StateId newPersisted)
    {
        int pruned = _repo.PruneBefore(newPersisted);
        if (pruned > 0 && _logger.IsDebug)
            _logger.Debug($"Pruned {pruned} persisted snapshots before block {newPersisted.BlockNumber}");
    }

    private void DoConvert(ConversionCandidate candidate)
    {
        if (candidate.Compacted is not null)
        {
            // Branch A — boundary CompactSize compacted: convert every in-memory base in the
            // range it spans and queue them for batched compaction. The CompactSize persistable
            // is produced by the batched compactor (a linked merge of the bases), not here, so
            // the compacted in-memory snapshot is used only to delimit the block range.
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
                            // Pre-leased return — dispose the caller's lease immediately;
                            // the repository's dict entry holds its own lease.
                            _repo.ConvertSnapshotToPersistedSnapshot(snap).Dispose();
                            _persistedSnapshotConvertTime.WithLabels("base").Observe(Stopwatch.GetTimestamp() - sw);
                            snap.Dispose();
                        }
                    });

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
                // Pre-leased return — dispose the caller's lease immediately;
                // the repository's dict entry holds its own lease.
                _repo.ConvertSnapshotToPersistedSnapshot(baseSnap).Dispose();
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

        // Persist all snapshots from current persisted state to latest. Flush ignores the
        // finality gate but still prefers the finalized state as the BFS seed when one is
        // available — that biases the walk onto the canonical chain. Falls back to the in-memory
        // tip when no finalized state root is exposed for the current finalized block.
        while (currentPersistedState.BlockNumber < latestStateId.Value.BlockNumber)
        {
            StateId? seed = null;
            long finalizedBlockNumber = _finalizedStateProvider.FinalizedBlockNumber;
            if (finalizedBlockNumber > currentPersistedState.BlockNumber)
            {
                Hash256? finalizedStateRoot = _finalizedStateProvider.GetFinalizedStateRootAt(finalizedBlockNumber);
                if (finalizedStateRoot is not null)
                    seed = new StateId(finalizedBlockNumber, finalizedStateRoot);
            }
            seed ??= _snapshotRepository.LastRegisteredState;
            if (seed is null) break;

            (PersistedSnapshot? persisted, Snapshot? snapshotToPersist) =
                TryFindSnapshotToPersist(seed.Value, currentPersistedState);

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

        // A linked persistable's NodeRefs scatter across the base snapshots' blob arenas, so
        // the HSST scan below reads blobs out of order. Prefetch every base's contiguous RLP
        // region up front so the kernel can stream them in as bulk read-ahead; once the
        // persistable is written the same regions are dropped from the page cache (below) —
        // they won't be read again. The leases are held for the whole method.
        using PersistedSnapshotList bases = _repo.LeaseBaseSnapshotsInRange(snapshot.From, snapshot.To);
        foreach (PersistedSnapshot baseSnapshot in bases)
            baseSnapshot.AdviseWillNeedBlobRange();

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

        // The persistable is now in RocksDB — drop the prefetched base blob ranges from the
        // page cache rather than leaving them hot until the base snapshots are pruned.
        foreach (PersistedSnapshot baseSnapshot in bases)
            baseSnapshot.AdviseDontNeedBlobRange();

        Metrics.FlatPersistenceTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

}
