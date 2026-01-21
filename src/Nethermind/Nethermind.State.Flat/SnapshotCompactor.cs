// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat;

public class SnapshotCompactor(
    IFlatDbConfig config,
    ResourcePool resourcePool,
    ISnapshotRepository snapshotRepository,
    ILogManager logManager)
{
    private readonly int _compactSize = config.CompactSize;
    private readonly int _midCompactSize = config.MidCompactSize;
    private readonly ILogger _logger = logManager.GetClassLogger<SnapshotCompactor>();

    private Counter _tooSlowSkip = DevMetric.Factory.CreateCounter("snapshot_compactor_too_slow", "too slow");

    public void DoCompactSnapshot(Snapshot snapshot)
    {
        using SnapshotPooledList snapshots = GetSnapshotsToCompact(snapshot);
        if (snapshots.Count == 0) return;

        Snapshot compactedSnapshot = CompactSnapshotBundle(snapshots);
        if (!snapshotRepository.TryAddCompactedSnapshot(compactedSnapshot))
        {
            compactedSnapshot.Dispose();
            return;
        }
    }

    private Gauge MidCompactCount = DevMetric.Factory.CreateGauge("mid_compact_count", "mcc");

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
            StateId? last = snapshotRepository.GetLastSnapshotId();
            if (last != null && last.Value.BlockNumber - blockNumber > 1)
            {
                _tooSlowSkip.Inc();
                // To slow. Just skip this block number.
                return SnapshotPooledList.Empty();
            }

            // Save memory by removing the compacted state from previous mid compaction
            foreach (var id in snapshotRepository.GetStatesAtBlockNumber(blockNumber - _compactSize))
            {
                if (snapshotRepository.RemoveAndReleaseCompactedKnownState(id))
                {
                    MidCompactCount.Dec();
                }
            }
        }

        // So the compact size change if its midCompact or fullCompact. The reason being its faster and use less memory
        // however, it increase the average snapshot count per bundle. Hard to know if its better or now.
        int compactSize = isMidCompaction ? _midCompactSize : _compactSize;

        int mode = int.Parse(Environment.GetEnvironmentVariable("MID_COMPACT_MODE") ?? "1");
        if (mode == 2)
        {
            compactSize = _compactSize;
        }

        long startingBlockNumber = ((blockNumber - 1) / compactSize) * compactSize;
        var snapshots = snapshotRepository.AssembleSnapshotsUntil(snapshot.To, startingBlockNumber, compactSize);

        bool snapshotsOk = false;
        try
        {
            if (snapshots.Count == 0)
            {
                return SnapshotPooledList.Empty();
            }

            if (snapshots[0].From.BlockNumber != startingBlockNumber)
            {
                // Could happen especially at start where the block may not be aligned, but not a big problem.
                if (_logger.IsDebug) _logger.Debug($"Unable to compile snapshots to compact. {snapshots[0].From.BlockNumber} -> {snapshots[^1].To.BlockNumber}. Snarting block number should be {startingBlockNumber}");

                return SnapshotPooledList.Empty();
            }

            // Nothing to combine if its just one
            if (snapshots.Count == 1) return SnapshotPooledList.Empty();

            if (isMidCompaction)
            {
                MidCompactCount.Inc();
            }

            snapshotsOk = true;
            return snapshots;
        }
        finally
        {
            if (!snapshotsOk)
            {
                snapshots.Dispose();
            }
        }
    }

    public Snapshot CompactSnapshotBundle(SnapshotPooledList snapshots)
    {
        StateId to = snapshots[^1].To;
        StateId from = snapshots[0].From;

        ResourcePool.Usage usage = (to.BlockNumber % _compactSize == 0)
            ? ResourcePool.Usage.Compactor
            : ResourcePool.Usage.MidCompactor;

        Snapshot snapshot = resourcePool.CreateSnapshot(from, to, usage);
        ConcurrentDictionary<AddressAsKey, Account?> accounts = snapshot.Content.Accounts;
        ConcurrentDictionary<(AddressAsKey, UInt256), SlotValue?> storages = snapshot.Content.Storages;
        ConcurrentDictionary<AddressAsKey, bool> selfDestructedStorageAddresses = snapshot.Content.SelfDestructedStorageAddresses;
        ConcurrentDictionary<(Hash256AsKey, TreePath), TrieNode> storageNodes = snapshot.Content.StorageNodes;
        ConcurrentDictionary<TreePath, TrieNode> stateNodes = snapshot.Content.StateNodes;

        HashSet<Address> addressToClear = new HashSet<Address>();
        HashSet<Hash256AsKey> addressHashToClear = new HashSet<Hash256AsKey>();

        for (int i = 0; i < snapshots.Count; i++)
        {
            var knownState = snapshots[i];
            foreach (var knownStateAccount in knownState.Accounts)
            {
                Address address = knownStateAccount.Key;
                accounts[address] = knownStateAccount.Value;
            }

            addressToClear.Clear();
            addressHashToClear.Clear();

            foreach (KeyValuePair<AddressAsKey, bool> addrK in knownState.SelfDestructedStorageAddresses)
            {
                var address = addrK.Key;
                var isNewAccount = addrK.Value;
                if (!isNewAccount)
                {
                    selfDestructedStorageAddresses[address] = false;
                    addressToClear.Add(address);
                    addressHashToClear.Add(address.Value.ToAccountPath.ToCommitment());
                }
                else
                {
                    // Note, if its already false, we should not set it to true
                    selfDestructedStorageAddresses.TryAdd(address, true);
                }
            }

            if (addressToClear.Count > 0)
            {
                // Clear
                foreach (var kv in storages)
                {
                    if (addressToClear.Contains(kv.Key.Item1))
                    {
                        storages.Remove(kv.Key, out _);
                    }
                }

                foreach (var kv in storageNodes)
                {
                    if (addressHashToClear.Contains(kv.Key.Item1))
                    {
                        storageNodes.Remove(kv.Key, out _);
                    }
                }
            }

            foreach (var knownStateStorage in knownState.Storages)
            {
                storages[knownStateStorage.Key] = knownStateStorage.Value;
            }

            foreach (var kv in knownState.StateNodes)
            {
                stateNodes[kv.Key] = kv.Value;
            }

            foreach (var kv in knownState.StorageNodes)
            {
                storageNodes[kv.Key] = kv.Value;
            }
        }

        return snapshot;
    }


}
