// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
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

        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, usage);
        ConcurrentDictionary<HashedKey<Address>, Account?> accounts = snapshot.Content.Accounts;
        ConcurrentDictionary<HashedKey<(Address, UInt256)>, SlotValue?> storages = snapshot.Content.Storages;
        ConcurrentDictionary<HashedKey<Address>, bool> selfDestructedStorageAddresses = snapshot.Content.SelfDestructedStorageAddresses;
        ConcurrentDictionary<HashedKey<(Hash256, TreePath)>, TrieNode> storageNodes = snapshot.Content.StorageNodes;
        ConcurrentDictionary<HashedKey<TreePath>, TrieNode> stateNodes = snapshot.Content.StateNodes;

        using ArrayPoolListRef<Task> compactTask = new(2);

        // Precompute self-destruct clear boundaries and the merged self-destruct map in one pass.
        // For each self-destructed (cleared) address, the boundary is the highest snapshot index that clears
        // it: a slot or storage node written before that index is cleared by the self-destruct and never
        // re-added, so the merge can simply skip it instead of adding it and then removing it with a full
        // dictionary scan. Slots are keyed by address, storage trie nodes by the account-path hash, matching
        // how each collection is keyed. Both maps stay null until a self-destruct is seen, keeping the common
        // (no self-destruct) case on the plain AddOrUpdateRange fast path.
        Dictionary<Address, int>? slotClearBoundary = null;
        Dictionary<Hash256, int>? nodeClearBoundary = null;
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach ((HashedKey<Address> address, bool isNewAccount) in snapshots[i].SelfDestructedStorageAddresses)
            {
                if (isNewAccount)
                {
                    // Note, if it's already false, we should not set it to true, hence the TryAdd
                    selfDestructedStorageAddresses.TryAdd(address, true);
                }
                else
                {
                    selfDestructedStorageAddresses[address] = false;
                    // i is ascending, so the last write wins and holds the highest clearing index.
                    (slotClearBoundary ??= [])[address.Key] = i;
                    (nodeClearBoundary ??= [])[address.Key.ToAccountPath.ToCommitment()] = i;
                }
            }
        }

        // Accounts
        compactTask.Add(Task.Run(() =>
        {
            for (int i = 0; i < snapshots.Count; i++)
            {
                Snapshot knownState = snapshots[i];
                accounts.AddOrUpdateRange(knownState.Accounts);
            }
        }));

        // Slots
        compactTask.Add(Task.Run(() =>
        {
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (slotClearBoundary is null)
                {
                    storages.AddOrUpdateRange(snapshots[i].Storages);
                    continue;
                }

                foreach ((HashedKey<(Address, UInt256)> key, SlotValue? value) in snapshots[i].Storages)
                {
                    if (slotClearBoundary.TryGetValue(key.Key.Item1, out int boundary) && i < boundary)
                        continue;
                    storages[key] = value;
                }
            }
        }));

        // State tries
        for (int i = 0; i < snapshots.Count; i++)
            stateNodes.AddOrUpdateRange(snapshots[i].StateNodes);

        // Storage tries
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (nodeClearBoundary is null)
            {
                storageNodes.AddOrUpdateRange(snapshots[i].StorageNodes);
                continue;
            }

            foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> kvp in snapshots[i].StorageNodes)
            {
                if (nodeClearBoundary.TryGetValue(kvp.Key.Key.Item1, out int boundary) && i < boundary)
                    continue;
                storageNodes[kvp.Key] = kvp.Value;
            }
        }

        Task.WaitAll(compactTask.AsSpan());

        return snapshot;
    }


}
