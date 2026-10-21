// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Collections;
using Nethermind.State.Flat.NodeBuffer;

namespace Nethermind.State.Flat;

public class SnapshotCompactor(
    IFlatDbConfig config,
    ICompactionSchedule schedule,
    IResourcePool resourcePool,
    ISnapshotRepository snapshotRepository,
    ILogManager logManager) : ISnapshotCompactor
{
    private readonly ulong _compactSize = config.CompactSize;
    private readonly ICompactionSchedule _schedule = schedule;
    private readonly ILogger _logger = logManager.GetClassLogger<SnapshotCompactor>();
    private readonly IResourcePool _resourcePool = resourcePool;
    private readonly ISnapshotRepository _snapshotRepository = snapshotRepository;

    public bool DoCompactSnapshot(in StateId stateId)
    {
        if (_snapshotRepository.TryLeaseInMemoryState(stateId, SnapshotTier.InMemoryBase, out Snapshot? snapshot))
        {
            using Snapshot _ = snapshot;

            long sw = Stopwatch.GetTimestamp();
            using SnapshotPooledList snapshots = GetSnapshotsToCompact(snapshot);

            if (snapshots.Count != 0)
            {
                Snapshot compactedSnapshot = CompactSnapshotBundle(snapshots);
                if (_snapshotRepository.TryAdd(compactedSnapshot, SnapshotTier.InMemoryCompacted))
                {
                    Metrics.CompactTime.Observe(Stopwatch.GetTimestamp() - sw);

                    return true;
                }
                else
                {
                    compactedSnapshot.Dispose();
                    return false;
                }
            }

        }

        return false;
    }

    public SnapshotPooledList GetSnapshotsToCompact(Snapshot snapshot)
    {
        ulong blockNumber = snapshot.To.BlockNumber;
        ulong compactSize = _schedule.GetCompactSize(blockNumber);
        if (compactSize <= 1) return SnapshotPooledList.Empty();
        bool isFullCompaction = compactSize == _compactSize;

        if (!isFullCompaction)
        {
            // Save memory by removing the compacted state from previous compaction
            using ArrayPoolList<StateId> previousStates = _snapshotRepository.GetStatesAtBlockNumber(blockNumber - _compactSize);
            foreach (StateId id in previousStates)
            {
                _snapshotRepository.RemoveAndReleaseInMemoryKnownState(id, SnapshotTier.InMemoryCompacted);
            }
        }

        // blockNumber < compactSize wraps startingBlockNumber below genesis; the assembly policy's
        // signed-height comparison reads it back as the intended "below genesis" bound.
        ulong startingBlockNumber = blockNumber - compactSize;
        SnapshotPooledList snapshots = _snapshotRepository.AssembleInMemorySnapshotsForCompaction(snapshot.To, startingBlockNumber, (int)compactSize);

        bool snapshotsOk = false;
        try
        {
            if (snapshots.Count == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping snapshot compaction at block {blockNumber}: assembled 0 of expected {compactSize} snapshots from start {startingBlockNumber}.");
                return SnapshotPooledList.Empty();
            }

            if (snapshots[0].From.BlockNumber != startingBlockNumber)
            {
                // Could happen especially at start where the block may not be aligned, but not a big problem.
                if (_logger.IsDebug) _logger.Debug($"Skipping snapshot compaction at block {blockNumber}: got {snapshots.Count} snapshots ({snapshots[0].From.BlockNumber} -> {snapshots[^1].To.BlockNumber}), expected start at {startingBlockNumber}.");

                return SnapshotPooledList.Empty();
            }

            // Nothing to combine if it's just one
            if (snapshots.Count == 1)
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping snapshot compaction at block {blockNumber}: got only 1 of expected {compactSize} snapshots from start {startingBlockNumber}.");
                return SnapshotPooledList.Empty();
            }

            snapshotsOk = true;
            return snapshots;
        }
        finally
        {
            if (!snapshotsOk) snapshots.Dispose();
        }
    }

    public Snapshot CompactSnapshotBundle(SnapshotPooledList snapshots)
    {
        StateId to = snapshots[^1].To;
        StateId from = snapshots[0].From;

        ulong compactSize = _schedule.GetCompactSize(to.BlockNumber);
        ResourcePool.Usage usage = ResourcePool.CompactUsage(compactSize);
        int count = snapshots.Count;

        // A slot written before the highest snapshot index that clears its address is dropped by the merge
        // rather than added then removed.
        using PooledDictionary<HashedKey<Address>, bool> selfDestructMerged = new();
        using PooledDictionary<Address, int> slotClearBoundary = new();
        for (int i = 0; i < count; i++)
        {
            foreach ((HashedKey<Address> address, bool isNewAccount) in snapshots[i].SelfDestructedStorageAddresses)
            {
                if (isNewAccount)
                {
                    // Note, if it's already false, we should not set it to true, hence the TryAdd
                    selfDestructMerged.TryAdd(address, true);
                }
                else
                {
                    selfDestructMerged[address] = false;
                    slotClearBoundary[address.Key] = i;
                }
            }
        }

        SortedSnapshotContent content = _resourcePool.GetSortedSnapshotContent(usage);
        try
        {
            using ArrayPoolListRef<Task> compactTask = new(2);
            try
            {
                compactTask.Add(Task.Run(() => MergeInto(
                    content.SortedAccounts, snapshots, default(AddressKeyComparer), static m => m.SortedAccounts, static c => c.Accounts)));
                compactTask.Add(Task.Run(() => MergeInto(
                    content.SortedStorages, snapshots, default(StorageKeyComparer), static m => m.SortedStorages, static c => c.Storages,
                    new StorageBoundaryKeep<Address, UInt256>(slotClearBoundary))));
                MergeRoots(content, snapshots);
                content.SortedSelfDestructs.BuildFromUnsorted(selfDestructMerged, default(AddressKeyComparer));
            }
            finally
            {
                Task.WaitAll(compactTask.AsSpan());
            }
        }
        catch
        {
            _resourcePool.ReturnSortedSnapshotContent(usage, content); // don't leak the pooled buffers on failure
            throw;
        }

        // The oldest pin covers every merged snapshot, whose pins may be released before the compacted snapshot.
        return new Snapshot(from, to, content, _resourcePool, usage, OldestBufferPin(snapshots)?.Copy());
    }

    private static void MergeRoots(SortedSnapshotContent content, SnapshotPooledList snapshots)
    {
        Snapshot final = snapshots[^1];
        content.StateRootPointer = final.StateRootPointer;
        if (content.StateRootPointer == 0 && final.To.StateRoot != ValueKeccak.EmptyTreeHash)
        {
            for (int i = snapshots.Count - 2; i >= 0; i--)
            {
                Snapshot source = snapshots[i];
                if (source.To.StateRoot == final.To.StateRoot && source.StateRootPointer != 0)
                {
                    content.StateRootPointer = source.StateRootPointer;
                    break;
                }
            }
        }

        foreach (Snapshot source in snapshots)
        {
            foreach ((HashedKey<Address> address, _) in source.SelfDestructedStorageAddresses)
                content.StorageRoots[address.Key.ToAccountPath.ToCommitment()] = 0;
            foreach ((Hash256 address, ulong root) in source.StorageRoots)
                content.StorageRoots[address] = root;
        }
    }

    private static INodeRecordBufferPin? OldestBufferPin(SnapshotPooledList snapshots)
    {
        INodeRecordBufferPin? oldest = null;
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (snapshots[i].BufferPin is { } pin && (oldest is null || pin.WriteBoundary < oldest.WriteBoundary)) oldest = pin;
        }

        return oldest;
    }

    private static void MergeInto<TKey, TValue, TComparer>(
        SortedMergeDictionary<TKey, TValue> target,
        SnapshotPooledList snapshots,
        TComparer comparer,
        Func<SortedSnapshotContent, SortedMergeDictionary<TKey, TValue>> fromSorted,
        Func<SnapshotContent, IReadOnlyCollection<KeyValuePair<TKey, TValue>>> fromMutable)
        where TKey : IEquatable<TKey>
        where TComparer : IComparer<TKey>
        => MergeInto(target, snapshots, comparer, fromSorted, fromMutable, default(KeepAll<TKey>));

    private static void MergeInto<TKey, TValue, TComparer, TKeep>(
        SortedMergeDictionary<TKey, TValue> target,
        SnapshotPooledList snapshots,
        TComparer comparer,
        Func<SortedSnapshotContent, SortedMergeDictionary<TKey, TValue>> fromSorted,
        Func<SnapshotContent, IReadOnlyCollection<KeyValuePair<TKey, TValue>>> fromMutable,
        TKeep keep)
        where TKey : IEquatable<TKey>
        where TComparer : IComparer<TKey>
        where TKeep : struct, IMergeKeep<TKey>
    {
        int count = snapshots.Count;
        SortedMergeDictionary<TKey, TValue>.Run[] sources = new SortedMergeDictionary<TKey, TValue>.Run[count];

        // Mutable inputs are sorted into transients that are disposed once the merge has copied them.
        List<SortedMergeDictionary<TKey, TValue>.PooledRun>? transients = null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                Snapshot source = snapshots[i];
                if (source.IsSorted)
                {
                    sources[i] = fromSorted(source.SortedContent).AsRun();
                }
                else
                {
                    SortedMergeDictionary<TKey, TValue>.PooledRun transient =
                        SortedMergeDictionary<TKey, TValue>.BuildRunFromUnsorted(fromMutable(source.Content), comparer);
                    sources[i] = transient.AsRun();
                    (transients ??= []).Add(transient);
                }
            }

            target.BuildFromMerge(sources, comparer, keep);
        }
        finally
        {
            if (transients is not null)
                foreach (SortedMergeDictionary<TKey, TValue>.PooledRun transient in transients) transient.Dispose();
        }
    }

    private struct StorageBoundaryKeep<TGroup, TSecondary>(PooledDictionary<TGroup, int> boundaries)
        : IMergeKeep<HashedKey<(TGroup, TSecondary)>>
        where TGroup : class, IEquatable<TGroup>
    {
        private TGroup? _group;
        private int _boundary;
        private bool _hasBoundary;

        public bool Keep(int sourceIndex, HashedKey<(TGroup, TSecondary)> key)
        {
            TGroup group = key.Key.Item1;
            if (!ReferenceEquals(group, _group) && (_group is null || !group.Equals(_group)))
            {
                _group = group;
                _hasBoundary = boundaries.TryGetValue(group, out _boundary);
            }
            return !_hasBoundary || sourceIndex >= _boundary;
        }
    }
}
