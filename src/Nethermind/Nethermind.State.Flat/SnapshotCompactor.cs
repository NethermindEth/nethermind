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
    private readonly int _midCompactSize;
    private readonly bool _useLogarithmicCompaction;
    private readonly ILogger _logger;
    private readonly IResourcePool _resourcePool;
    private readonly ISnapshotRepository _snapshotRepository;

    public SnapshotCompactor(IFlatDbConfig config,
        IResourcePool resourcePool,
        ISnapshotRepository snapshotRepository,
        ILogManager logManager)
    {
        _useLogarithmicCompaction = config.UseLogarithmicCompaction;

        if (_useLogarithmicCompaction)
        {
            if (config.CompactSize > 1 && (config.CompactSize & (config.CompactSize - 1)) != 0)
                throw new ArgumentException("Compact size must be a power of 2");
            if (config.MinCompactSize > 1 && (config.MinCompactSize & (config.MinCompactSize - 1)) != 0)
                throw new ArgumentException("Min compact size must be a power of 2");
            if (config.MinCompactSize > config.CompactSize)
                throw new ArgumentException("Min compact size must be <= compact size");
        }
        else
        {
            if (config.CompactSize % config.MidCompactSize != 0)
                throw new ArgumentException("Compact size must be divisible by mid compact size");
        }

        _resourcePool = resourcePool;
        _snapshotRepository = snapshotRepository;
        _compactSize = config.CompactSize;
        _minCompactSize = Math.Max(config.MinCompactSize, 2);
        _midCompactSize = config.MidCompactSize;
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

        long startingBlockNumber;
        int actualCompactSize;

        if (_useLogarithmicCompaction)
        {
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

            startingBlockNumber = ((blockNumber - 1) / compactSize) * compactSize;
            actualCompactSize = compactSize;
        }
        else
        {
            bool isFullCompaction = blockNumber % _compactSize == 0;
            bool isMidCompaction = !isFullCompaction && blockNumber % _midCompactSize == 0;
            if (!isFullCompaction && !isMidCompaction) return SnapshotPooledList.Empty();

            if (isMidCompaction)
            {
                // Save memory by removing the compacted state from previous mid compaction
                foreach (StateId id in _snapshotRepository.GetStatesAtBlockNumber(blockNumber - _midCompactSize))
                {
                    if (_snapshotRepository.RemoveAndReleaseCompactedKnownState(id))
                    {
                    }
                }
            }

            // Always anchor to the last compactSize-aligned block
            startingBlockNumber = ((blockNumber - 1) / _compactSize) * _compactSize;
            actualCompactSize = (int)(blockNumber - startingBlockNumber);
        }

        SnapshotPooledList snapshots = _snapshotRepository.AssembleSnapshotsUntil(snapshot.To, startingBlockNumber, actualCompactSize);

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

        ResourcePool.Usage usage;
        if (_useLogarithmicCompaction)
        {
            int compactSize = (int)Math.Min(to.BlockNumber & -to.BlockNumber, _compactSize);
            usage = ResourcePool.CompactUsage(compactSize);
        }
        else
        {
            usage = (to.BlockNumber % _compactSize == 0)
                ? ResourcePool.Usage.Compactor
                : ResourcePool.Usage.MidCompactor;
        }

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
