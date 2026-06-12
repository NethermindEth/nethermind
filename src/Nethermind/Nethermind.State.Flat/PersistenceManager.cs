// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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
    IResourcePool resourcePool,
    ILogManager logManager) : IPersistenceManager
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistenceManager>();
    private readonly int _minReorgDepth = configuration.MinReorgDepth;
    private readonly int _maxReorgDepth = configuration.MaxReorgDepth;
    private readonly int _compactSize = configuration.CompactSize;
    private readonly bool _earlyPersist = configuration.EarlyPersist;
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

    private Snapshot? GetFinalizedSnapshotAtBlockNumber(long blockNumber, StateId currentPersistedState, bool compactedSnapshot)
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

    private Snapshot? GetFirstSnapshotAtBlockNumber(long blockNumber, StateId currentPersistedState, bool compactedSnapshot)
    {
        using ArrayPoolList<StateId> states = snapshotRepository.GetStatesAtBlockNumber(blockNumber);
        foreach (StateId stateId in states)
        {
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
                if (_logger.IsWarn) _logger.Warn($"Force persisting state {stateId}");

                return snapshot;
            }

            snapshot.Dispose();
        }

        return null;
    }

    internal Snapshot? DetermineSnapshotToPersist(StateId latestSnapshot)
    {
        // Actually, the latest compacted snapshot, not the latest snapshot.
        long lastSnapshotNumber = latestSnapshot.BlockNumber;

        StateId currentPersistedState = GetCurrentPersistedStateId();
        long finalizedBlockNumber = finalizedStateProvider.FinalizedBlockNumber;
        long inMemoryStateDepth = lastSnapshotNumber - currentPersistedState.BlockNumber;
        if (!_earlyPersist && inMemoryStateDepth - _compactSize < _minReorgDepth)
        {
            // Keep some state in memory. With early persist the finalization gate below is the sole
            // gate; historical state for snap serving is kept as reverse diffs instead.
            return null;
        }

        Snapshot? snapshotToPersist;

        long nextCompactedBoundary = _schedule.NextFullCompactionAfter(currentPersistedState.BlockNumber);
        if (nextCompactedBoundary > finalizedBlockNumber)
        {
            if (inMemoryStateDepth <= _maxReorgDepth)
            {
                // Unfinalized, and still under max reorg depth
                return null;
            }

            if (_logger.IsWarn) _logger.Warn($"Very long unfinalized state. Force persisting to conserve memory. finalized block number is {finalizedBlockNumber}.");
            snapshotToPersist = GetFirstSnapshotAtBlockNumber(nextCompactedBoundary, currentPersistedState, true) ??
                                GetFirstSnapshotAtBlockNumber(currentPersistedState.BlockNumber + 1, currentPersistedState, false);
        }
        else
        {
            snapshotToPersist = GetFinalizedSnapshotAtBlockNumber(nextCompactedBoundary, currentPersistedState, true) ??
                                GetFinalizedSnapshotAtBlockNumber(currentPersistedState.BlockNumber + 1, currentPersistedState, false);
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
        StateId? latestStateId = snapshotRepository.GetLastSnapshotId();

        if (latestStateId is null)
        {
            return currentPersistedState;
        }

        // Persist all snapshots from current persisted state to latest
        while (currentPersistedState.BlockNumber < latestStateId.Value.BlockNumber)
        {
            long nextCompactedBoundary = _schedule.NextFullCompactionAfter(currentPersistedState.BlockNumber);

            // Try finalized snapshots first (compacted, then non-compacted)
            Snapshot? snapshotToPersist = GetFinalizedSnapshotAtBlockNumber(
                nextCompactedBoundary,
                currentPersistedState,
                compactedSnapshot: true);

            snapshotToPersist ??= GetFinalizedSnapshotAtBlockNumber(
                currentPersistedState.BlockNumber + 1,
                currentPersistedState,
                compactedSnapshot: false);

            // Fall back to the first available snapshot if finalized not available
            snapshotToPersist ??= GetFirstSnapshotAtBlockNumber(
                nextCompactedBoundary,
                currentPersistedState,
                compactedSnapshot: true);

            snapshotToPersist ??= GetFirstSnapshotAtBlockNumber(
                currentPersistedState.BlockNumber + 1,
                currentPersistedState,
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
        long compactLength = snapshot.To.BlockNumber! - snapshot.From.BlockNumber!;

        // Usually at the start of the application
        if (compactLength != _compactSize && _logger.IsTrace) _logger.Trace($"Persisting non compacted state of length {compactLength}");

        Snapshot? reverseDiff = null;
        IPersistence.IPersistenceReader? oldStateReader = null;
        if (_earlyPersist)
        {
            if (HasIrreversibleSelfDestruct(snapshot))
            {
                // Reversing a self-destruct of an account with persisted storage would need all its old
                // slots and storage trie nodes, which is unbounded. Collapse the serving window instead;
                // it restarts at the new persisted state. Post EIP-6780 this effectively never happens.
                snapshotRepository.ClearHistory();
                Metrics.HistoricalWindowTruncations++;
                if (_logger.IsDebug) _logger.Debug($"Snapshot {snapshot.To} self-destructs an account with persisted storage; truncating the historical serving window.");
            }
            else
            {
                // The reader sees the pre-batch state for the whole batch, so old values can be captured
                // next to each overwriting write.
                oldStateReader = persistence.CreateReader();
                reverseDiff = resourcePool.CreateSnapshot(from: snapshot.To, to: snapshot.From, ResourcePool.Usage.ReverseDiff);
            }
        }

        long sw = Stopwatch.GetTimestamp();
        try
        {
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
                    if (reverseDiff is not null) reverseDiff.Content.Accounts[kv.Key] = oldStateReader!.GetAccount(kv.Key.Key);

                    batch.SetAccount(kv.Key.Key, kv.Value);
                }

                foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> kv in snapshot.Storages)
                {
                    (Address addr, UInt256 slot) = kv.Key.Key;

                    if (reverseDiff is not null)
                    {
                        SlotValue oldValue = new();
                        reverseDiff.Content.Storages[kv.Key] = oldStateReader!.TryGetSlot(addr, slot, ref oldValue) ? oldValue : null;
                    }

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

                    if (reverseDiff is not null)
                    {
                        // A node absent at the old state is never reached when traversing from an old
                        // root, so absent keys need no marker.
                        byte[]? oldRlp = oldStateReader!.TryLoadStateRlp(path, ReadFlags.None);
                        if (oldRlp is not null) reverseDiff.Content.StateNodes[new HashedKey<TreePath>(path)] = CreateOldStateNode(oldRlp);
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

                    if (reverseDiff is not null)
                    {
                        byte[]? oldRlp = oldStateReader!.TryLoadStorageRlp(address, path, ReadFlags.None);
                        if (oldRlp is not null) reverseDiff.Content.StorageNodes[new HashedKey<(Hash256, TreePath)>((address, path))] = CreateOldStateNode(oldRlp);
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
        }
        catch
        {
            reverseDiff?.Dispose();
            throw;
        }
        finally
        {
            oldStateReader?.Dispose();
        }

        // Registered only after the batch commits so a reader can never see the diff alongside the old
        // persisted state. The opposite window (new state, diff not yet registered) is covered by the
        // bundle gather retry.
        if (reverseDiff is not null && !snapshotRepository.TryAddReverseDiff(reverseDiff))
        {
            reverseDiff.Dispose();
        }

        Metrics.FlatPersistenceTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

    private static bool HasIrreversibleSelfDestruct(Snapshot snapshot)
    {
        foreach (KeyValuePair<HashedKey<Address>, bool> toSelfDestructStorage in snapshot.SelfDestructedStorageAddresses)
        {
            // false marks an account whose storage already reached persistence; true is a same-tx
            // created account with nothing on disk (nothing to reverse).
            if (!toSelfDestructStorage.Value) return true;
        }

        return false;
    }

    private static TrieNode CreateOldStateNode(byte[] rlp) =>
        new(NodeType.Unknown, Keccak.Compute(rlp), rlp) { IsPersisted = true };
}
