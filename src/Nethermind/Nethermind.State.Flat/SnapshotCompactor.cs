// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat;

public class SnapshotCompactor
{
    private readonly ILogger _logger;
    private readonly FlatDiffRepository _flatDiffRepository;
    private readonly ResourcePool _resourcePool;

    private readonly int _compactSize;
    private readonly int _compactEveryBlockNum;
    private readonly Histogram _flatdiffimes;
    private static Gauge _compactedMemory = DevMetric.Factory.CreateGauge("flatdiff_compacted_memory", "memory", "category");

    public SnapshotCompactor(FlatDiffRepository flatDiffRepository, FlatDiffRepository.Configuration config, ResourcePool resourcePool, ILogManager logManager)
    {
        _flatDiffRepository = flatDiffRepository;
        _resourcePool = resourcePool;
        _logger = logManager.GetClassLogger<SnapshotCompactor>();
        _flatdiffimes = FlatDiffRepository._flatdiffimes;

        _compactSize = config.CompactSize;
        _compactEveryBlockNum = config.CompactInterval;
    }

    internal void CompactLevel(StateId stateId)
    {
        try
        {
            if (_compactSize <= 1) return; // Disabled
            long blockNumber = stateId.blockNumber;
            if (blockNumber == 0) return;
            if (blockNumber % _compactSize != 0)
            {
                StateId? last = _flatDiffRepository.GetLastSnapshotId();
                if (last != null && last.Value.blockNumber - blockNumber > 1)
                {
                    // To slow. Just skip this block number.
                    return;
                }

                if (blockNumber % _compactEveryBlockNum != 0) return;
            }

            // Release the compacted state first so that its in the resourcce pool
            if (stateId.blockNumber % _compactSize != 0)
            {
                // Save memory
                foreach (var id in _flatDiffRepository.GetStatesAtBlockNumber(stateId.blockNumber - _compactSize))
                {
                    _flatDiffRepository.RemoveAndReleaseCompactedKnownState(id);
                }
            }

            long startingBlockNumber = ((blockNumber - 1) / _compactSize) * _compactSize;
            long sw = Stopwatch.GetTimestamp();
            Snapshot compactedSnapshot;

            StateId current = stateId;
            using ArrayPoolList<Snapshot> snapshots = new((int)_compactSize);
            try
            {

                while(_flatDiffRepository.TryLeaseCompactedState(current, out Snapshot? snapshot) || _flatDiffRepository.TryLeaseState(current, out snapshot))
                {
                    if (_logger.IsTrace) _logger.Trace($"Got {snapshot.From} -> {snapshot.To}");

                    if (snapshot.From.blockNumber < startingBlockNumber)
                    {
                        // The non-compacted one
                        snapshot.Dispose();
                        if (!_flatDiffRepository.TryLeaseState(current, out snapshot))
                        {
                            break;
                        }
                    }

                    snapshots.Add(snapshot);
                    if (snapshot.From == current) {
                        break; // Some test commit two block with the same id, so we dont know the parent anymore.
                    }

                    current = snapshot.From;
                    if (snapshot.From.blockNumber == startingBlockNumber)
                    {
                        break;
                    }
                }
                snapshots.Reverse();

                if (snapshots.Count == 0) return;
                if (snapshots[0].From.blockNumber != startingBlockNumber)
                {
                    _logger.Warn($"Unable to compile snapshots to compact. {snapshots[0].From.blockNumber} -> {snapshots[^1].To.blockNumber}");
                    // unable to compile list of snapshot for the whole thing
                    return;
                }

                // Nothing to combine if its  just one
                if (snapshots.Count == 1)
                {
                    return;
                }

                if (_logger.IsDebug) _logger.Debug($"Compacting {stateId}");
                sw = Stopwatch.GetTimestamp();
                compactedSnapshot = CompactSnapshotBundle(snapshots);
            }
            finally
            {
                foreach (Snapshot snapshot in snapshots)
                {
                    snapshot.Dispose();
                }
            }

            _flatdiffimes.WithLabels("compaction", "compact_to_known_state").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();
            Dictionary<MemoryType, long> memory = compactedSnapshot.EstimateMemory();

            _flatdiffimes.WithLabels("compaction", "add_repolock").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            if (_logger.IsDebug) _logger.Debug($"Compacted {snapshots.Count} to {stateId}");

            if (_flatDiffRepository.AddCompactedSnapshot(stateId, compactedSnapshot))
            {
                foreach (var keyValuePair in memory)
                {
                    _compactedMemory.WithLabels(keyValuePair.Key.ToString()).Inc(keyValuePair.Value);
                }
                _compactedMemory.WithLabels("count").Inc(1);
            }
            else
            {
                compactedSnapshot.Dispose();
            }

            _flatdiffimes.WithLabels("compaction", "add_and_measure").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            _flatdiffimes.WithLabels("compaction", "cleanup_compacted").Observe(Stopwatch.GetTimestamp() - sw);
        }
        catch (Exception e)
        {
            _logger.Error($"Compactor failed {e}");
        }
    }

    public Snapshot CompactSnapshotBundle(ArrayPoolList<Snapshot> snapshots)
    {
        StateId to = snapshots[^1].To;
        StateId from = snapshots[0].From;

        IFlatDiffRepository.SnapshotBundleUsage usage = (to.blockNumber % _compactSize == 0)
            ? IFlatDiffRepository.SnapshotBundleUsage.Compactor
            : IFlatDiffRepository.SnapshotBundleUsage.MidCompactor;

        Snapshot snapshot = _resourcePool.CreateSnapshot(from, to, usage);
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
