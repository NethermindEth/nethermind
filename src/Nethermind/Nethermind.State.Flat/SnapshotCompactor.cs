// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public class SnapshotCompactor : ISnapshotCompactor
{
    private readonly int _compactSize;
    private readonly int _minCompactSize;
    private readonly ILogger _logger;
    private readonly IResourcePool _resourcePool;
    private readonly ISnapshotRepository _snapshotRepository;

    public SnapshotCompactor(IFlatDbConfig config,
        IResourcePool resourcePool,
        ISnapshotRepository snapshotRepository,
        ILogManager logManager)
    {
        if (config.CompactSize > 1 && (config.CompactSize & (config.CompactSize - 1)) != 0)
            throw new ArgumentException("Compact size must be a power of 2");
        if (config.MinCompactSize > 1 && (config.MinCompactSize & (config.MinCompactSize - 1)) != 0)
            throw new ArgumentException("Min compact size must be a power of 2");
        if (config.MinCompactSize > config.CompactSize)
            throw new ArgumentException("Min compact size must be <= compact size");

        _resourcePool = resourcePool;
        _snapshotRepository = snapshotRepository;
        _compactSize = config.CompactSize;
        _minCompactSize = Math.Max(config.MinCompactSize, 2);
        _logger = logManager.GetClassLogger<SnapshotCompactor>();
    }

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
        if (_compactSize <= 1) return SnapshotPooledList.Empty(); // Disabled
        long blockNumber = snapshot.To.BlockNumber;
        if (blockNumber == 0) return SnapshotPooledList.Empty();

        int compactSize = (int)Math.Min(blockNumber & -blockNumber, _compactSize);
        if (compactSize < _minCompactSize) return SnapshotPooledList.Empty();
        bool isFullCompaction = compactSize == _compactSize;

        if (!isFullCompaction)
        {
            // Save memory by removing the compacted state from previous compaction
            foreach (StateId id in _snapshotRepository.GetStatesAtBlockNumber(blockNumber - _compactSize))
            {
                if (_snapshotRepository.RemoveAndReleaseCompactedKnownState(id))
                {
                }
            }
        }

        long startingBlockNumber = ((blockNumber - 1) / compactSize) * compactSize;
        SnapshotPooledList snapshots = _snapshotRepository.AssembleSnapshotsUntil(snapshot.To, startingBlockNumber, compactSize);

        bool snapshotsOk = false;
        try
        {
            if (snapshots.Count == 0) return SnapshotPooledList.Empty();

            if (snapshots[0].From.BlockNumber != startingBlockNumber)
            {
                // Could happen especially at start where the block may not be aligned, but not a big problem.
                if (_logger.IsDebug) _logger.Debug($"Unable to compile snapshots to compact. {snapshots[0].From.BlockNumber} -> {snapshots[^1].To.BlockNumber}. Starting block number should be {startingBlockNumber}");

                return SnapshotPooledList.Empty();
            }

            // Nothing to combine if it's just one
            if (snapshots.Count == 1) return SnapshotPooledList.Empty();

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

        int compactSize = (int)Math.Min(to.BlockNumber & -to.BlockNumber, _compactSize);
        ResourcePool.Usage usage = ResourcePool.CompactUsage(compactSize);

        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, usage);
        ConcurrentDictionary<HashedKey<Address>, Account?> accounts = snapshot.Content.Accounts;
        ConcurrentDictionary<HashedKey<(Address, UInt256)>, SlotValue?> storages = snapshot.Content.Storages;
        ConcurrentDictionary<HashedKey<Address>, bool> selfDestructedStorageAddresses = snapshot.Content.SelfDestructedStorageAddresses;
        ConcurrentDictionary<HashedKey<(Hash256, TreePath)>, TrieNode> storageNodes = snapshot.Content.StorageNodes;
        ConcurrentDictionary<HashedKey<TreePath>, TrieNode> stateNodes = snapshot.Content.StateNodes;

        using ArrayPoolListRef<Task> compactTask = new ArrayPoolListRef<Task>(2);

        // Accounts
        compactTask.Add(Task.Run(() =>
        {
            for (int i = 0; i < snapshots.Count; i++)
            {
                Snapshot knownState = snapshots[i];
                accounts.AddOrUpdateRange(knownState.Accounts);
            }
        }));

        // Slots and Selfdestruct
        compactTask.Add(Task.Run(() =>
        {
            using PooledSet<Address> addressToClear = new();

            for (int i = 0; i < snapshots.Count; i++)
            {
                Snapshot knownState = snapshots[i];
                addressToClear.Clear();

                foreach ((HashedKey<Address> address, var isNewAccount) in knownState.SelfDestructedStorageAddresses)
                {
                    if (isNewAccount)
                    {
                        // Note, if it's already false, we should not set it to true, hence the TryAdd
                        selfDestructedStorageAddresses.TryAdd(address, true);
                    }
                    else
                    {
                        selfDestructedStorageAddresses[address] = false;
                        addressToClear.Add(address.Key);
                    }
                }

                if (addressToClear.Count > 0)
                {
                    // Clear
                    foreach ((HashedKey<(Address, UInt256)> key, SlotValue? _) in storages)
                    {
                        if (addressToClear.Contains(key.Key.Item1))
                        {
                            storages.TryRemove(key, out _);
                        }
                    }
                }

                storages.AddOrUpdateRange(knownState.Storages);
            }
        }));

        // State tries
        for (int i = 0; i < snapshots.Count; i++)
            stateNodes.AddOrUpdateRange(snapshots[i].StateNodes);

        // Storage tries
        for (int i = 0; i < snapshots.Count; i++)
        {
            // Clear storage nodes for self-destructed accounts
            using PooledSet<Hash256> addressHashToClear = new();
            foreach ((HashedKey<Address> address, var isNewAccount) in snapshots[i].SelfDestructedStorageAddresses)
            {
                if (!isNewAccount)
                    addressHashToClear.Add(address.Key.ToAccountPath.ToCommitment());
            }

            if (addressHashToClear.Count > 0)
            {
                foreach (HashedKey<(Hash256, TreePath)> key in storageNodes.Keys)
                {
                    if (addressHashToClear.Contains(key.Key.Item1))
                        storageNodes.TryRemove(key, out _);
                }
            }

            storageNodes.AddOrUpdateRange(snapshots[i].StorageNodes);
        }

        Task.WaitAll(compactTask.AsSpan());

        return snapshot;
    }


}
