// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Nethermind.State.Flat.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.State.Flat;

public class PersistenceManager(
    IFlatDbConfig configuration,
    ICompactionSchedule compactionSchedule,
    IFinalizedStateProvider finalizedStateProvider,
    IPersistence persistence,
    ISnapshotRepository snapshotRepository,
    ILogManager logManager) : IPersistenceManager
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistenceManager>();
    private readonly ulong _minReorgDepth = configuration.MinReorgDepth;
    private readonly ulong _maxReorgDepth = configuration.MaxReorgDepth;
    private readonly ulong _compactSize = configuration.CompactSize;
    private readonly ICompactionSchedule _schedule = compactionSchedule;
    private readonly List<(Hash256, TreePath)> _trieNodesSortBuffer = []; // Presort make it faster
    private readonly Lock _persistenceLock = new();

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

    /// <summary>The first block number that has not yet been persisted, starting from genesis (block 0) when nothing is persisted.</summary>
    private static ulong NextBlockToPersist(StateId state) =>
        state == StateId.PreGenesis ? 0UL : state.BlockNumber + 1;

    private Snapshot? GetFinalizedSnapshotAtBlockNumber(ulong blockNumber, StateId currentPersistedState, bool compactedSnapshot)
    {
        Hash256? finalizedStateRoot = finalizedStateProvider.GetFinalizedStateRootAt(blockNumber);
        using ArrayPoolList<StateId> states = snapshotRepository.GetStatesAtBlockNumber(blockNumber);
        foreach (StateId stateId in states)
        {
            if (stateId.StateRoot != finalizedStateRoot) continue;

            Snapshot? snapshot;
            if (compactedSnapshot)
            {
                if (!snapshotRepository.TryLeaseCompactedState(stateId, out snapshot)) continue;
            }
            else
            {
                if (!snapshotRepository.TryLeaseState(stateId, out snapshot)) continue;
            }

            if (snapshot.From == currentPersistedState)
            {
                if (_logger.IsDebug) _logger.Debug($"Persisting compacted state {stateId}");

                return snapshot;
            }

            snapshot.Dispose();
        }

        return null;
    }

    private Snapshot? GetHeadAncestorAtBlockNumber(ulong blockNumber, StateId currentPersistedState, in StateId head, bool compactedSnapshot)
    {
        // Pick the state at blockNumber that is the head's ancestor rather than an arbitrary fork, so the
        // forced persist follows the chain leading to the head instead of orphaning it.
        if (!snapshotRepository.TryFindAncestorStateAtBlock(head, blockNumber, out StateId stateId))
            return null;

        Snapshot? snapshot;
        if (compactedSnapshot)
        {
            if (!snapshotRepository.TryLeaseCompactedState(stateId, out snapshot)) return null;
        }
        else
        {
            if (!snapshotRepository.TryLeaseState(stateId, out snapshot)) return null;
        }

        if (snapshot.From == currentPersistedState)
        {
            if (_logger.IsWarn) _logger.Warn($"Force persisting state {stateId}");

            return snapshot;
        }

        snapshot.Dispose();
        return null;
    }

    internal Snapshot? DetermineSnapshotToPersist(StateId latestSnapshot)
    {
        // Actually, the latest compacted snapshot, not the latest snapshot.
        ulong lastSnapshotNumber = latestSnapshot.BlockNumber;

        StateId currentPersistedState = GetCurrentPersistedStateId();
        ulong finalizedBlockNumber = finalizedStateProvider.FinalizedBlockNumber;

        Debug.Assert(currentPersistedState == StateId.PreGenesis || lastSnapshotNumber >= currentPersistedState.BlockNumber,
            "Latest snapshot must be at or ahead of the last persisted block.");
        ulong inMemoryStateDepth = currentPersistedState == StateId.PreGenesis
            ? lastSnapshotNumber + 1
            : lastSnapshotNumber - currentPersistedState.BlockNumber;

        if (inMemoryStateDepth.SaturatingSub(_compactSize) < _minReorgDepth)
        {
            return null;
        }

        Snapshot? snapshotToPersist;

        ulong nextCompactedBoundary = _schedule.NextFullCompactionAfter(currentPersistedState.BlockNumber);
        if (nextCompactedBoundary > finalizedBlockNumber)
        {
            if (inMemoryStateDepth <= _maxReorgDepth)
            {
                // Unfinalized, and still under max reorg depth
                return null;
            }

            if (_logger.IsWarn) _logger.Warn($"Very long unfinalized state. Force persisting to conserve memory. finalized block number is {finalizedBlockNumber}.");
            // Follow the committed head; fall back to the longest chain when nothing was committed this session.
            StateId head = snapshotRepository.GetLastCommittedStateId() ?? snapshotRepository.GetLastSnapshotId() ?? latestSnapshot;
            snapshotToPersist = GetHeadAncestorAtBlockNumber(nextCompactedBoundary, currentPersistedState, head, true) ??
                                GetHeadAncestorAtBlockNumber(NextBlockToPersist(currentPersistedState), currentPersistedState, head, false);
        }
        else
        {
            snapshotToPersist = GetFinalizedSnapshotAtBlockNumber(nextCompactedBoundary, currentPersistedState, true) ??
                                GetFinalizedSnapshotAtBlockNumber(NextBlockToPersist(currentPersistedState), currentPersistedState, false);
        }

        if (snapshotToPersist is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Unable to find snapshot to persist. Current persisted state {currentPersistedState}. Compact size {_compactSize}.");
        }

        return snapshotToPersist;
    }

    public void AddToPersistence(StateId latestSnapshot)
    {
        using Lock.Scope scope = _persistenceLock.EnterScope();
        // Attempt to add snapshots into bigcache
        while (true)
        {
            Snapshot? snapshotToSave = DetermineSnapshotToPersist(latestSnapshot);

            if (snapshotToSave is null) return;
            using Snapshot _ = snapshotToSave; // dispose

            snapshotRepository.RemoveSiblingAndDescendents(snapshotToSave.To);

            // Add the canon snapshot
            PersistSnapshot(snapshotToSave);
            _currentPersistedStateId = snapshotToSave.To;
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
        // Follow the committed head; fall back to the longest chain when nothing was committed this session.
        StateId? latestStateId = snapshotRepository.GetLastCommittedStateId() ?? snapshotRepository.GetLastSnapshotId();

        if (latestStateId is null)
        {
            return currentPersistedState;
        }

        // Persist all snapshots from current persisted state to latest
        while (currentPersistedState == StateId.PreGenesis || currentPersistedState.BlockNumber < latestStateId.Value.BlockNumber)
        {
            ulong nextCompactedBoundary = _schedule.NextFullCompactionAfter(currentPersistedState.BlockNumber);

            // Try finalized snapshots first (compacted, then non-compacted)
            Snapshot? snapshotToPersist = GetFinalizedSnapshotAtBlockNumber(
                nextCompactedBoundary,
                currentPersistedState,
                compactedSnapshot: true);

            snapshotToPersist ??= GetFinalizedSnapshotAtBlockNumber(
                NextBlockToPersist(currentPersistedState),
                currentPersistedState,
                compactedSnapshot: false);

            // Fall back to the head's chain if finalized not available
            snapshotToPersist ??= GetHeadAncestorAtBlockNumber(
                nextCompactedBoundary,
                currentPersistedState,
                latestStateId.Value,
                compactedSnapshot: true);

            snapshotToPersist ??= GetHeadAncestorAtBlockNumber(
                NextBlockToPersist(currentPersistedState),
                currentPersistedState,
                latestStateId.Value,
                compactedSnapshot: false);

            if (snapshotToPersist is null)
            {
                break;
            }

            using Snapshot _ = snapshotToPersist;

            snapshotRepository.RemoveSiblingAndDescendents(snapshotToPersist.To);

            PersistSnapshot(snapshotToPersist);
            _currentPersistedStateId = snapshotToPersist.To;
            currentPersistedState = _currentPersistedStateId;
        }

        return currentPersistedState;
    }

    public void ResetPersistedStateId()
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        _currentPersistedStateId = reader.CurrentState;
    }

    internal void PersistSnapshot(Snapshot snapshot)
    {
        ulong compactLength = snapshot.To.BlockNumber - snapshot.From.BlockNumber;

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
}
