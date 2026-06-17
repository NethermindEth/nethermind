// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using WholeReadScanner = Nethermind.State.Flat.PersistedSnapshots.PersistedSnapshotScanner<
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSession,
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSessionReader,
    Nethermind.State.Flat.Hsst.NoOpPin>;

[assembly: InternalsVisibleTo("Nethermind.State.Flat.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.State.Flat;

public class PersistenceManager(
    IFlatDbConfig configuration,
    ICompactionSchedule schedule,
    IFinalizedStateProvider finalizedStateProvider,
    IPersistence persistence,
    ISnapshotRepository snapshotRepository,
    ILogManager logManager,
    IPersistedSnapshotCompactor compactor,
    IPersistedSnapshotLoader loader,
    IProcessExitSource processExitSource) : IPersistenceManager, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistenceManager>();
    // Linked to process exit so the conversion Parallel.ForEach below cancels at shutdown-start —
    // before DI disposal order matters — letting the owning FlatDbManager.RunPersistence task drain.
    private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);
    private readonly int _minReorgDepth = configuration.MinReorgDepth;
    private readonly int _maxInMemoryBaseSnapshotCount = configuration.MaxInMemoryBaseSnapshotCount;
    // Force-persist backstop depth: the long-finality window when enabled (the persisted tier serves
    // deep reorgs), otherwise the smaller non-long-finality MaxReorgDepth.
    private readonly int _backstopReorgDepth = configuration.EnableLongFinality
        ? configuration.LongFinalityMaxReorgDepth
        : configuration.MaxReorgDepth;
    private readonly int _compactSize = configuration.CompactSize;
    private readonly bool _enableLongFinality = configuration.EnableLongFinality;
    private readonly List<(Hash256, TreePath)> _trieNodesSortBuffer = []; // Presort make it faster
    // SemaphoreSlim rather than a Lock: the AddToPersistence drain awaits the compactor's async
    // Enqueue while holding the mutex, which a Lock.Scope (a ref struct) cannot span.
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);

    private StateId _currentPersistedStateId = StateId.PreGenesis;

    public IPersistence.IPersistenceReader LeaseReader() => persistence.CreateReader();

    public StateId GetCurrentPersistedStateId()
    {
        if (_currentPersistedStateId == StateId.PreGenesis)
        {
            using IPersistence.IPersistenceReader reader = persistence.CreateReader();
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
    ///   <item>Else if <c>snapshotsDepth &gt; </c> the backstop depth (<c>LongFinalityMaxReorgDepth</c>
    ///   when long finality is enabled, otherwise <c>MaxReorgDepth</c>; finalization stalled) → seed =
    ///   the committed head.</item>
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
        StateId? seed = null;
        bool forcedByBackstop = false;
        long finalizedBlockNumber = finalizedStateProvider.FinalizedBlockNumber;
        long nextBoundary = schedule.NextFullCompactionAfter(currentPersistedState.BlockNumber);
        if (finalizedBlockNumber >= nextBoundary
            && snapshotsDepth + _compactSize > _minReorgDepth)
        {
            // Anchor at the next boundary block, not at the CL-reported finalized tip. The
            // outer gate guarantees boundary <= finalizedBlockNumber, so the provider's own
            // range check passes; the boundary is below chain head by construction, so the
            // canonical header is in the block tree and FindHeader resolves.
            long targetBlockNumber = nextBoundary;
            Hash256? canonicalRoot = finalizedStateProvider.GetFinalizedStateRootAt(targetBlockNumber);
            if (canonicalRoot is not null)
                seed = new StateId(targetBlockNumber, canonicalRoot);
        }
        else if (snapshotsDepth > _backstopReorgDepth)
        {
            // Backstop (finalization stalled): seed from the committed head so the forced persist
            // follows the canonical chain rather than an arbitrary/longest fork (which
            // RemoveSiblingAndDescendents would then orphan). Falls back to the longest chain only
            // when nothing was committed this session.
            seed = snapshotRepository.GetLastCommittedStateId() ?? snapshotRepository.LastRegisteredState;
            forcedByBackstop = true;
        }

        if (seed is not null)
        {
            (PersistedSnapshot? persisted, Snapshot? inMemory) =
                snapshotRepository.FindSnapshotToPersist(seed.Value, currentPersistedState, _compactSize);
            if (persisted is not null || inMemory is not null)
            {
                // Warn only when the backstop (not the normal finalized trigger) actually forces this
                // persist — not when the backstop seed finds no candidate and we fall through to the
                // Phase 2 persisted-snapshot conversion below.
                if (forcedByBackstop && _logger.IsWarn) _logger.Warn(
                    $"In-memory state depth {snapshotsDepth} exceeded the force-persist backstop {_backstopReorgDepth} " +
                    $"with finality stalled (finalized block {finalizedBlockNumber}). Forcing persistence to bound memory.");
                return (persisted, inMemory, null);
            }
        }

        // ---- Phase 2: conversion to the persisted-snapshot tier ----
        if (!_enableLongFinality) return (null, null, null);
        if (snapshotRepository.SnapshotCount <= _maxInMemoryBaseSnapshotCount) return (null, null, null);

        return (null, null, TryFindSnapshotToConvert(currentPersistedState));
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
        using ArrayPoolList<StateId> ordered = snapshotRepository.GetStatesUpToBlock(long.MaxValue);

        // Pass 1 (global): boundary-CompactSize in-memory compacted → Branch A.
        foreach (StateId X in ordered)
        {
            if (!snapshotRepository.TryLeaseInMemoryState(X, SnapshotTier.InMemoryCompacted, out Snapshot? compacted)) continue;

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
            if (!snapshotRepository.TryLeaseInMemoryState(X, SnapshotTier.InMemoryBase, out Snapshot? baseSnap)) continue;

            if (IsOnDisk(baseSnap!.From, currentPersistedState))
            {
                return new ConversionCandidate(Compacted: null, baseSnap);
            }
            baseSnap.Dispose();
        }

        return null;
    }

    private bool IsOnDisk(in StateId state, in StateId currentPersistedState) =>
        state == currentPersistedState || snapshotRepository.HasBaseSnapshot(state);

    internal sealed record ConversionCandidate(Snapshot? Compacted, Snapshot? Base);

    public async Task AddToPersistence(StateId latestSnapshot)
    {
        await _persistenceLock.WaitAsync();
        try
        {
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
                    snapshotRepository.RemoveSiblingAndDescendents(toPersist.To);
                    PersistSnapshot(toPersist);
                    _currentPersistedStateId = toPersist.To;
                    snapshotRepository.RemoveStatesUntil(toPersist.To.BlockNumber);
                }
                else if (persistedToPersist is not null)
                {
                    using PersistedSnapshot _ = persistedToPersist;
                    snapshotRepository.RemoveSiblingAndDescendents(persistedToPersist.To);
                    PersistPersistedSnapshot(persistedToPersist);
                    _currentPersistedStateId = persistedToPersist.To;
                    snapshotRepository.RemoveStatesUntil(persistedToPersist.To.BlockNumber);
                }
                else if (toConvert?.Compacted is not null)
                {
                    await ConvertCompactedRange(toConvert.Compacted);
                }
                else if (toConvert?.Base is not null)
                {
                    await ConvertSingleBase(toConvert.Base);
                }
                else
                {
                    break;
                }
            }
        }
        finally
        {
            _persistenceLock.Release();
        }
    }

    /// <summary>
    /// Branch A — boundary CompactSize compacted: convert every in-memory base in the range it
    /// spans and queue them for batched compaction. The CompactSized snapshot is produced by the
    /// batched compactor (a linked merge of the bases), not here, so the compacted in-memory
    /// snapshot is used only to delimit the block range. Disposes <paramref name="compacted"/>.
    /// </summary>
    private async Task ConvertCompactedRange(Snapshot compacted)
    {
        try
        {
            long start = compacted.From.BlockNumber + 1;
            long end = compacted.To.BlockNumber;

            ArrayPoolList<StateId> allStateIds = new(64);
            for (long b = start; b <= end; b++)
            {
                using ArrayPoolList<StateId> statesAtBlock = snapshotRepository.GetStatesAtBlockNumber(b);
                foreach (StateId state in statesAtBlock)
                    allStateIds.Add(state);
            }

            Parallel.ForEach(
                allStateIds,
                new ParallelOptions { CancellationToken = _cts.Token },
                state =>
                {
                    if (snapshotRepository.TryLeaseInMemoryState(state, SnapshotTier.InMemoryBase, out Snapshot? snap))
                    {
                        long sw = Stopwatch.GetTimestamp();
                        loader.ConvertAndRegister(snap);
                        Metrics.PersistedSnapshotConvertTime.Observe(Stopwatch.GetTimestamp() - sw);
                        snap.Dispose();
                    }
                });

            // Remove exactly the converted in-memory snapshots — not RemoveStatesUntil(end),
            // which would also drop snapshots added concurrently within the block range. Must
            // run before the channel handoff below: the compactor takes ownership of
            // allStateIds and disposes it.
            foreach (StateId state in allStateIds)
            {
                // A To can exist in both in-memory tiers — remove from each.
                snapshotRepository.RemoveAndReleaseInMemoryKnownState(state, SnapshotTier.InMemoryCompacted);
                snapshotRepository.RemoveAndReleaseInMemoryKnownState(state, SnapshotTier.InMemoryBase);
            }

            await compactor.EnqueueAsync(allStateIds, _cts.Token);
        }
        finally
        {
            compacted.Dispose();
        }
    }

    /// <summary>
    /// Branch B — single base convert (fragmented case: no full-CompactSize compacted available
    /// for the candidate range yet). Disposes <paramref name="baseSnap"/>.
    /// </summary>
    private async Task ConvertSingleBase(Snapshot baseSnap)
    {
        try
        {
            long sw = Stopwatch.GetTimestamp();
            loader.ConvertAndRegister(baseSnap);
            Metrics.PersistedSnapshotConvertTime.Observe(Stopwatch.GetTimestamp() - sw);

            ArrayPoolList<StateId> single = new(1) { baseSnap.To };
            await compactor.EnqueueAsync(single, _cts.Token);

            snapshotRepository.RemoveAndReleaseInMemoryKnownState(baseSnap.To, SnapshotTier.InMemoryBase);
        }
        finally
        {
            baseSnap.Dispose();
        }
    }

    /// <summary>
    /// Walk and persist every snapshot up to the current tip, ignoring the finality gate, and return
    /// the resulting persisted state.
    /// </summary>
    /// <remarks>
    /// Called only by the genesis loader (via <c>FlatDbManager.FlushCache</c>), for sync compatibility:
    /// it advances the persisted RocksDB state all the way to the tip and prunes both tiers behind it,
    /// leaving only the persisted state that the sync pipeline reads directly. Unlike
    /// <see cref="AddToPersistence"/> it has no per-call drain bound and seeds the walk from the
    /// finalized state when available, falling back to the in-memory then tier-aware latest tip.
    /// </remarks>
    public StateId FlushToPersistence()
    {
        _persistenceLock.Wait();
        try
        {
            return FlushToPersistenceLocked();
        }
        finally
        {
            _persistenceLock.Release();
        }
    }

    private StateId FlushToPersistenceLocked()
    {
        StateId currentPersistedState = GetCurrentPersistedStateId();
        // Follow the committed head; fall back to the longest chain when nothing was committed this session.
        StateId? latestStateId = snapshotRepository.GetLastCommittedStateId() ?? snapshotRepository.GetLastSnapshotId();

        if (latestStateId is null)
        {
            return currentPersistedState;
        }

        // Persist all snapshots from current persisted state to latest. Flush ignores the
        // finality gate but still prefers the finalized state as the BFS seed when one is
        // available — that biases the walk onto the canonical chain. Falls back to the committed
        // head (then the longest chain) when no finalized state root is exposed, which also covers
        // a persisted-only backlog after the in-memory tier has been drained.
        while (currentPersistedState.BlockNumber < latestStateId.Value.BlockNumber)
        {
            StateId? seed = null;
            long finalizedBlockNumber = finalizedStateProvider.FinalizedBlockNumber;
            if (finalizedBlockNumber > currentPersistedState.BlockNumber)
            {
                Hash256? finalizedStateRoot = finalizedStateProvider.GetFinalizedStateRootAt(finalizedBlockNumber);
                if (finalizedStateRoot is not null)
                    seed = new StateId(finalizedBlockNumber, finalizedStateRoot);
            }
            // Fall back to the committed head (latestStateId folds in GetLastCommittedStateId, then the
            // longest chain) so the forced walk follows the canonical chain rather than a longer
            // non-canonical fork, and still covers a persisted-only backlog once the in-memory tier drains.
            seed ??= latestStateId;
            if (seed is null) break;

            (PersistedSnapshot? persisted, Snapshot? snapshotToPersist) =
                snapshotRepository.FindSnapshotToPersist(seed.Value, currentPersistedState, _compactSize);

            if (persisted is not null)
            {
                using PersistedSnapshot persistedScope = persisted;
                snapshotRepository.RemoveSiblingAndDescendents(persisted.To);
                PersistPersistedSnapshot(persisted);
                _currentPersistedStateId = persisted.To;
                currentPersistedState = _currentPersistedStateId;
                snapshotRepository.RemoveStatesUntil(persisted.To.BlockNumber);
                continue;
            }

            if (snapshotToPersist is null) break;

            using Snapshot inMemScope = snapshotToPersist;

            snapshotRepository.RemoveSiblingAndDescendents(snapshotToPersist.To);
            PersistSnapshot(snapshotToPersist);
            _currentPersistedStateId = snapshotToPersist.To;
            currentPersistedState = _currentPersistedStateId;
            snapshotRepository.RemoveStatesUntil(snapshotToPersist.To.BlockNumber);
        }

        return currentPersistedState;
    }

    public void ResetPersistedStateId()
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        _currentPersistedStateId = reader.CurrentState;
    }

    public void Dispose()
    {
        _cts.Dispose();
        _persistenceLock.Dispose();
    }

    internal void PersistSnapshot(Snapshot snapshot)
    {
        long compactLength = snapshot.To.BlockNumber! - snapshot.From.BlockNumber!;

        // Usually at the start of the application
        if (compactLength != _compactSize && _logger.IsTrace) _logger.Trace($"Persisting non compacted state of length {compactLength}");

        long sw = Stopwatch.GetTimestamp();
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(snapshot.From, snapshot.To))
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
            // foreach (var tn in snapshot.TrieNodes)
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

                stateNodesSize += node.FullRlp.Length;
                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetStateTrieNode(path, node.FullRlp.AsSpan());

                node.IsPersisted = true;
                node.PrunePersistedRecursively(1);
            }

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StorageTrieNodeKeys);
            _trieNodesSortBuffer.Sort();

            long storageNodesSize = 0;
            // foreach (var tn in snapshot.TrieNodes)
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

                storageNodesSize += node.FullRlp.Length;
                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetStorageTrieNode(address, path, node.FullRlp.AsSpan());
                node.IsPersisted = true;
                node.PrunePersistedRecursively(1);
            }

            Metrics.FlatPersistenceSnapshotSize.Observe(stateNodesSize, labels: new StringLabel("state_nodes"));
            Metrics.FlatPersistenceSnapshotSize.Observe(storageNodesSize, labels: new StringLabel("storage_nodes"));
        }

        Metrics.FlatPersistenceTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

    internal void PersistPersistedSnapshot(PersistedSnapshot snapshot)
    {
        long sw = Stopwatch.GetTimestamp();

        // A linked CompactSized's NodeRefs scatter across the base snapshots' blob arenas, so
        // the HSST scan below reads blobs out of order. Prefetch every base's contiguous RLP
        // region up front so the kernel can stream them in as bulk read-ahead; once the
        // CompactSized is written the same regions are dropped from the page cache (below) —
        // they won't be read again. The leases are held for the whole method.
        using PersistedSnapshotList bases = snapshotRepository.LeaseBaseSnapshotsInRange(snapshot.From, snapshot.To);
        foreach (PersistedSnapshot baseSnapshot in bases)
            baseSnapshot.AdviseWillNeedBlobRange();

        using WholeReadSession session = snapshot.BeginWholeReadSession();
        WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, snapshot);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(snapshot.From, snapshot.To))
        {
            // Single walk over column 0x01: SD, account, and slot sub-tags all sit in the
            // same per-address inner HSST, so one outer pass + TryResolveAll resolves all
            // three for each address. Per-address ordering (SD before SetAccount/SetStorage)
            // is preserved within the row; cross-address ordering is irrelevant to the
            // write batch.
            foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
            {
                if (entry.SelfDestructFlag is false)
                    batch.SelfDestruct(entry.Address);

                if (entry.HasAccount)
                    batch.SetAccount(entry.Address, entry.Account);

                foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
                    batch.SetStorage(entry.Address, slot.Slot, slot.Value);
            }

            foreach (WholeReadScanner.StateNodeEntry entry in scanner.StateNodes)
                batch.SetStateTrieNode(entry.Path, entry.Rlp);

            foreach (WholeReadScanner.StorageNodeEntry entry in scanner.StorageNodes)
                batch.SetStorageTrieNode(entry.AddressHash.ToCommitment(), entry.Path, entry.Rlp);
        }

        // The CompactSized is now in RocksDB — drop the prefetched base blob ranges from the
        // page cache rather than leaving them hot until the base snapshots are pruned.
        foreach (PersistedSnapshot baseSnapshot in bases)
            baseSnapshot.AdviseDontNeedBlobRange();

        Metrics.FlatPersistenceTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

}
