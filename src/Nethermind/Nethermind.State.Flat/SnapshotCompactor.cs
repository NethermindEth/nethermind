// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Collections;
using Nethermind.Trie;

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
        if (_snapshotRepository.TryLeaseState(stateId, out Snapshot? snapshot))
        {
            using Snapshot _ = snapshot; // dispose

            // Actually do the compaction
            long sw = Stopwatch.GetTimestamp();
            using SnapshotPooledList snapshots = GetSnapshotsToCompact(snapshot);

            if (snapshots.Count != 0)
            {
                Snapshot compactedSnapshot = CompactSnapshotBundle(snapshots);
                if (_snapshotRepository.TryAddCompactedSnapshot(compactedSnapshot))
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
            // Save memory by removing the compacted state from previous compaction.
            foreach (StateId id in _snapshotRepository.GetStatesAtBlockNumber(blockNumber - _compactSize))
            {
                if (_snapshotRepository.RemoveAndReleaseCompactedKnownState(id))
                {
                }
            }
        }

        ulong startingBlockNumber = blockNumber - compactSize;
        SnapshotPooledList snapshots = _snapshotRepository.AssembleSnapshotsUntil(snapshot.To, startingBlockNumber, (int)compactSize);

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

        // A slot/node written before the highest snapshot index that clears its address is dropped by the merge
        // rather than added then removed. Boundary maps stay null until a self-destruct is seen.
        Dictionary<HashedKey<Address>, bool> selfDestructMerged = [];
        Dictionary<Address, int>? slotClearBoundary = null;
        Dictionary<Hash256, int>? nodeClearBoundary = null;
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
                    (slotClearBoundary ??= [])[address.Key] = i;
                    (nodeClearBoundary ??= [])[address.Key.ToAccountPath.ToCommitment()] = i;
                }
            }
        }

        Func<int, HashedKey<(Address, UInt256)>, bool>? slotKeep = slotClearBoundary is null
            ? null
            : (i, key) => !(slotClearBoundary.TryGetValue(key.Key.Item1, out int boundary) && i < boundary);
        Func<int, HashedKey<(Hash256, TreePath)>, bool>? nodeKeep = nodeClearBoundary is null
            ? null
            : (i, key) => !(nodeClearBoundary.TryGetValue(key.Key.Item1, out int boundary) && i < boundary);

        SortedSnapshotContent content = _resourcePool.GetSortedSnapshotContent(usage);

        using ArrayPoolListRef<Task> compactTask = new(4);
        compactTask.Add(Task.Run(() => MergeInto(
            content.SortedAccounts, snapshots, SnapshotKeyComparers.Address, static m => m.SortedAccounts, static c => c.Accounts, null)));
        compactTask.Add(Task.Run(() => MergeInto(
            content.SortedStorages, snapshots, SnapshotKeyComparers.Storage, static m => m.SortedStorages, static c => c.Storages, slotKeep)));
        compactTask.Add(Task.Run(() => MergeInto(
            content.SortedStateNodes, snapshots, SnapshotKeyComparers.StateNode, static m => m.SortedStateNodes, static c => c.StateNodes, null)));
        compactTask.Add(Task.Run(() => MergeInto(
            content.SortedStorageNodes, snapshots, SnapshotKeyComparers.StorageNode, static m => m.SortedStorageNodes, static c => c.StorageNodes, nodeKeep)));

        content.SortedSelfDestructs.BuildFromUnsorted(selfDestructMerged, SnapshotKeyComparers.Address);

        Task.WaitAll(compactTask.AsSpan());

        return new Snapshot(from, to, content, _resourcePool, usage);
    }

    public Snapshot ConvertToSorted(Snapshot source)
    {
        SnapshotContent mutable = source.Content;
        SortedSnapshotContent content = _resourcePool.GetSortedSnapshotContent(ResourcePool.Usage.ConvertedBase);

        content.SortedAccounts.BuildFromUnsorted(mutable.Accounts, SnapshotKeyComparers.Address);
        content.SortedStorages.BuildFromUnsorted(mutable.Storages, SnapshotKeyComparers.Storage);
        content.SortedSelfDestructs.BuildFromUnsorted(mutable.SelfDestructedStorageAddresses, SnapshotKeyComparers.Address);
        content.SortedStateNodes.BuildFromUnsorted(mutable.StateNodes, SnapshotKeyComparers.StateNode);
        content.SortedStorageNodes.BuildFromUnsorted(mutable.StorageNodes, SnapshotKeyComparers.StorageNode);

        return new Snapshot(source.From, source.To, content, _resourcePool, ResourcePool.Usage.ConvertedBase);
    }

    private static void MergeInto<TKey, TValue>(
        SortedMergeDictionary<TKey, TValue> target,
        SnapshotPooledList snapshots,
        IComparer<TKey> comparer,
        Func<SortedSnapshotContent, SortedMergeDictionary<TKey, TValue>> fromSorted,
        Func<SnapshotContent, IReadOnlyCollection<KeyValuePair<TKey, TValue>>> fromMutable,
        Func<int, TKey, bool>? keep) where TKey : IEquatable<TKey>
    {
        int count = snapshots.Count;
        SortedMergeDictionary<TKey, TValue>[] sources = new SortedMergeDictionary<TKey, TValue>[count];

        // Mutable inputs are sorted into transients that are disposed once the merge has copied them.
        List<SortedMergeDictionary<TKey, TValue>>? transients = null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                Snapshot source = snapshots[i];
                if (source.IsSorted)
                {
                    sources[i] = fromSorted(source.SortedContent);
                }
                else
                {
                    SortedMergeDictionary<TKey, TValue> transient = new();
                    transient.BuildFromUnsorted(fromMutable(source.Content), comparer);
                    sources[i] = transient;
                    (transients ??= []).Add(transient);
                }
            }

            target.BuildFromMerge(sources, comparer, keep);
        }
        finally
        {
            if (transients is not null)
                foreach (SortedMergeDictionary<TKey, TValue> transient in transients) transient.Dispose();
        }
    }
}
