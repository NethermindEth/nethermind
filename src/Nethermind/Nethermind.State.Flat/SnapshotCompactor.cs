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

public class SnapshotCompactor : ISnapshotCompactor
{
    private readonly int _compactSize;
    private readonly int _midCompactSize;
    private readonly ILogger _logger;
    private readonly IResourcePool _resourcePool;
    private readonly ISnapshotRepository _snapshotRepository;

    public SnapshotCompactor(IFlatDbConfig config,
        IResourcePool resourcePool,
        ISnapshotRepository snapshotRepository,
        ILogManager logManager)
    {
        if (config.CompactSize % config.MidCompactSize != 0) throw new ArgumentException("Compact size must be divisible by mid compact size");

        _resourcePool = resourcePool;
        _snapshotRepository = snapshotRepository;
        _compactSize = config.CompactSize;
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
                    StateId stateId1 = snapshot.To;
                    if (stateId1.BlockNumber % _compactSize == 0)
                    {
                        Metrics.CompactTime.Observe(Stopwatch.GetTimestamp() - sw);
                    }
                    else if (stateId1.BlockNumber % _midCompactSize == 0)
                    {
                        Metrics.MidCompactTime.Observe(Stopwatch.GetTimestamp() - sw);
                    }

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

        bool isFullCompaction = blockNumber % _compactSize == 0;
        bool isMidCompaction = !isFullCompaction && blockNumber % _midCompactSize == 0;
        if (!isFullCompaction && !isMidCompaction) return SnapshotPooledList.Empty();

        if (isMidCompaction)
        {
            // Save memory by removing the compacted state from previous mid compaction
            foreach (StateId id in _snapshotRepository.GetStatesAtBlockNumber(blockNumber - _compactSize))
            {
                if (_snapshotRepository.RemoveAndReleaseCompactedKnownState(id))
                {
                }
            }
        }

        // So the compact size change if its midCompact or fullCompact. The reason being mid-compaction is much smaller
        // and therefore faster and use less memory however, it increases the average snapshot count per bundle.
        // Hard to know if it's better or not now.
        int compactSize = isMidCompaction ? _midCompactSize : _compactSize;
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

        ResourcePool.Usage usage = (to.BlockNumber % _compactSize == 0)
            ? ResourcePool.Usage.Compactor
            : ResourcePool.Usage.MidCompactor;

        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, usage);
        ConcurrentDictionary<AddressAsKey, Account?> accounts = snapshot.Content.Accounts;
        ConcurrentDictionary<(AddressAsKey, UInt256), SlotValue?> storages = snapshot.Content.Storages;
        ConcurrentDictionary<AddressAsKey, bool> selfDestructedStorageAddresses = snapshot.Content.SelfDestructedStorageAddresses;
        ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> storageNodes = snapshot.Content.StorageNodes;
        ConcurrentDictionary<TreePath, TrieNode> stateNodes = snapshot.Content.StateNodes;

        HashSet<Address> addressToClear = new();
        HashSet<Hash256AsKey> addressHashToClear = new();

        for (int i = 0; i < snapshots.Count; i++)
        {
            Snapshot knownState = snapshots[i];
            accounts.AddOrUpdateRange(knownState.Accounts);

            addressToClear.Clear();
            addressHashToClear.Clear();

            foreach ((AddressAsKey address, var isNewAccount) in knownState.SelfDestructedStorageAddresses)
            {
                if (isNewAccount)
                {
                    // Note, if it's already false, we should not set it to true, hence the TryAdd
                    selfDestructedStorageAddresses.TryAdd(address, true);
                }
                else
                {
                    selfDestructedStorageAddresses[address] = false;
                    addressToClear.Add(address);
                    addressHashToClear.Add(address.Value.ToAccountPath.ToCommitment());
                }
            }

            if (addressToClear.Count > 0)
            {
                // Clear
                foreach (((AddressAsKey Address, UInt256) key, SlotValue? _) in storages)
                {
                    if (addressToClear.Contains(key.Address))
                    {
                        storages.Remove(key, out _);
                    }
                }

                foreach (((Hash256AsKey Hash, TreePath) key, TrieNode _) in storageNodes)
                {
                    if (addressHashToClear.Contains(key.Hash))
                    {
                        storageNodes.Remove(key, out _);
                    }
                }
            }

            storages.AddOrUpdateRange(knownState.Storages);
            stateNodes.AddOrUpdateRange(knownState.StateNodes);
            storageNodes.AddOrUpdateRange(knownState.StorageNodes);
        }

        return snapshot;
    }


}
