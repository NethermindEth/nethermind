// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Nethermind.State.Flat.Test")]

namespace Nethermind.State.Flat;

public class PersistenceManager(
    IFlatDbConfig configuration,
    IFinalizedStateProvider finalizedStateProvider,
    IPersistence persistence,
    ISnapshotRepository snapshotRepository,
    ILogManager logManager,
    IPersistedSnapshotCompactor persistedSnapshotCompactor,
    IPersistedSnapshotRepository persistedSnapshotRepository) : IPersistenceManager
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly int _minReorgDepth = configuration.MinReorgDepth;
    private readonly int _maxInMemoryReorgDepth = configuration.MaxInMemoryReorgDepth;
    private readonly int _longFinalityReorgDepth = configuration.LongFinalityReorgDepth;
    private readonly int _compactSize = configuration.CompactSize;
    private readonly IPersistence _persistence = persistence;
    private readonly ISnapshotRepository _snapshotRepository = snapshotRepository;
    private readonly IFinalizedStateProvider _finalizedStateProvider = finalizedStateProvider;
    private readonly IPersistedSnapshotCompactor _persistedSnapshotCompactor = persistedSnapshotCompactor;
    private readonly IPersistedSnapshotRepository _persistedSnapshotRepository = persistedSnapshotRepository;
    private readonly List<(Hash256AsKey, TreePath)> _trieNodesSortBuffer = new(); // Presort make it faster
    private readonly Lock _persistenceLock = new();

    private readonly Channel<StateId> _compactPersistedJobs = Channel.CreateBounded<StateId>(1);
    private readonly CancellationTokenSource _cancelTokenSource = new();
    private Task? _compactPersistedTask;

    private StateId _currentPersistedStateId = StateId.PreGenesis;

    private Task EnsureCompactorStarted() =>
        _compactPersistedTask ??= RunPersistedCompactor(_cancelTokenSource.Token);

    private async Task RunPersistedCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateId stateId in _compactPersistedJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    _persistedSnapshotCompactor.DoCompactSnapshot(stateId);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error compacting persisted snapshot. {ex}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cancelTokenSource.Cancel();
        _compactPersistedJobs.Writer.Complete();
        if (_compactPersistedTask is not null)
            await _compactPersistedTask;
        _cancelTokenSource.Dispose();
    }

    public IPersistence.IPersistenceReader LeaseReader() => _persistence.CreateReader();

    public StateId GetCurrentPersistedStateId()
    {
        if (_currentPersistedStateId == StateId.PreGenesis)
        {
            using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
            _currentPersistedStateId = reader.CurrentState;
        }
        return _currentPersistedStateId;
    }

    private (PersistedSnapshot? Persisted, Snapshot? InMemory) GetFinalizedSnapshotAtBlockNumber(long blockNumber, StateId currentPersistedState, bool compactedSnapshot)
    {
        Hash256? finalizedStateRoot = _finalizedStateProvider.GetFinalizedStateRootAt(blockNumber);
        using ArrayPoolList<StateId> states = _snapshotRepository.GetStatesAtBlockNumber(blockNumber);
        foreach (StateId stateId in states)
        {
            if (stateId.StateRoot != finalizedStateRoot) continue;

            Snapshot? snapshot;
            if (compactedSnapshot)
            {
                if (!_snapshotRepository.TryLeaseCompactedState(stateId, out snapshot)) continue;
            }
            else
            {
                if (!_snapshotRepository.TryLeaseState(stateId, out snapshot)) continue;
            }

            if (snapshot.From == currentPersistedState)
            {
                if (_logger.IsDebug) _logger.Debug($"Persisting compacted state {stateId}");

                return (null, snapshot);
            }

            snapshot.Dispose();
        }

        // No in-memory snapshot found — try persisted snapshot at same block/root
        if (finalizedStateRoot is not null)
        {
            StateId targetStateId = new StateId(blockNumber, finalizedStateRoot);
            bool found = compactedSnapshot
                ? _persistedSnapshotRepository.TryLeasePersistableCompactedSnapshotTo(targetStateId, out PersistedSnapshot? persisted)
                : _persistedSnapshotRepository.TryLeaseSnapshotTo(targetStateId, out persisted);
            if (found)
            {
                if (persisted!.From == currentPersistedState)
                    return (persisted, null);
                persisted.Dispose();
            }
        }

        return (null, null);
    }

    private Snapshot? GetFirstSnapshotAtBlockNumber(long blockNumber, StateId currentPersistedState, bool compactedSnapshot)
    {
        using ArrayPoolList<StateId> states = _snapshotRepository.GetStatesAtBlockNumber(blockNumber);
        foreach (StateId stateId in states)
        {
            Snapshot? snapshot;
            if (compactedSnapshot)
            {
                if (!_snapshotRepository.TryLeaseCompactedState(stateId, out snapshot)) continue;
            }
            else
            {
                if (!_snapshotRepository.TryLeaseState(stateId, out snapshot)) continue;
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

    internal (PersistedSnapshot? ToPersistPersistedSnapshot, Snapshot? ToPersist, long? snapshotLevelToConvert) DetermineSnapshotAction(StateId latestSnapshot)
    {
        long lastSnapshotNumber = latestSnapshot.BlockNumber;

        long? TryGetSnapshotLevelToConvert()
        {
            return _snapshotRepository.GetEarliestSnapshotId()?.BlockNumber;
        }

        StateId currentPersistedState = GetCurrentPersistedStateId();
        long finalizedBlockNumber = _finalizedStateProvider.FinalizedBlockNumber;
        long snapshotsDepth = lastSnapshotNumber - currentPersistedState.BlockNumber;
        if (snapshotsDepth - _compactSize < _minReorgDepth)
        {
            long? earliestInMemory = TryGetSnapshotLevelToConvert();
            if (earliestInMemory == null)
            {
                return (null, null, null);
            }

            long inMemoryDepth = lastSnapshotNumber - earliestInMemory.Value;
            if (inMemoryDepth <= _maxInMemoryReorgDepth + _compactSize)
            {
                // No action needed
                return (null, null, null);
            }

            return (null, null, TryGetSnapshotLevelToConvert());
        }

        long afterPersistPersistedBlockNumber = currentPersistedState.BlockNumber + _compactSize;
        if (afterPersistPersistedBlockNumber > finalizedBlockNumber)
        {
            if (snapshotsDepth <= _maxInMemoryReorgDepth)
            {
                // No action needed
                return (null, null, null);
            }

            if (snapshotsDepth > _longFinalityReorgDepth)
            {
                // Need to force persisted snapshot
                return (TryGetForcePersistedSnapshot(currentPersistedState, snapshotsDepth), null, null);
            }

            // Memory pressure with unfinalized state: convert to persisted snapshots instead of force-persisting to RocksDB
            if (_logger.IsWarn) _logger.Warn($"Very long unfinalized state. Converting to persisted snapshots. finalized block number is {finalizedBlockNumber}.");

            return (null, null, TryGetSnapshotLevelToConvert());
        }

        (PersistedSnapshot? persistedSnapshot, Snapshot? snapshotToPersist) =
            GetFinalizedSnapshotAtBlockNumber(currentPersistedState.BlockNumber + _compactSize, currentPersistedState, true);

        bool compactedSnapshot = true;
        if (snapshotToPersist is null && persistedSnapshot is null)
        {
            compactedSnapshot = false;
            (persistedSnapshot, snapshotToPersist) =
                GetFinalizedSnapshotAtBlockNumber(currentPersistedState.BlockNumber + 1, currentPersistedState, false);
        }

        if (snapshotToPersist is not null)
            return (null, snapshotToPersist, null);

        if (persistedSnapshot is not null)
        {
            if (compactedSnapshot)
            {
                _logger.Warn($"Persisting persisted snapshot {persistedSnapshot.From} to {persistedSnapshot.To}, is compacted snapshot {compactedSnapshot}. {currentPersistedState}");
            }
            return (persistedSnapshot, null, null);
        }

        if (_logger.IsWarn) _logger.Warn($"Unable to find snapshot to persist. Current persisted state {currentPersistedState}. Compact size {_compactSize}.");
        return (null, null, null);
    }

    public void AddToPersistence(StateId latestSnapshot)
    {
        using Lock.Scope scope = _persistenceLock.EnterScope();
        while (true)
        {
            (PersistedSnapshot? persistedToPersist, Snapshot? toPersist, long? snapshotLevelToConvert) = DetermineSnapshotAction(latestSnapshot);

            if (toPersist is not null)
            {
                using Snapshot _ = toPersist;
                PersistSnapshot(toPersist);
                _currentPersistedStateId = toPersist.To;
            }
            else if (snapshotLevelToConvert.HasValue)
            {
                using ArrayPoolList<StateId> snapshotIds = _snapshotRepository.GetStatesAtBlockNumber(snapshotLevelToConvert.Value);

                foreach (StateId state in snapshotIds)
                {
                    if (_snapshotRepository.TryLeaseState(state, out Snapshot? snapshot))
                    {
                        _persistedSnapshotRepository.ConvertSnapshotToPersistedSnapshot(snapshot);
                        snapshot.Dispose();
                    }

                    // Also convert compacted snapshot of size _compactSize as persistable
                    if (_snapshotRepository.TryLeaseCompactedState(state, out Snapshot? compacted))
                    {
                        if (compacted.To.BlockNumber - compacted.From.BlockNumber == _compactSize)
                        {
                            _persistedSnapshotRepository.ConvertSnapshotToPersistedSnapshot(compacted, isPersistable: true);

                            using PersistedSnapshotList existing = _persistedSnapshotRepository.AssembleSnapshotsForCompaction(compacted.To, compacted.From.BlockNumber);
                            for (int i = 0; i < existing.Count; i++)
                                existing[i].AdviseDontNeed();
                        }
                        compacted.Dispose();
                    }

                    EnsureCompactorStarted();
                    _compactPersistedJobs.Writer.WriteAsync(state).AsTask().Wait();
                }

                _snapshotRepository.RemoveStatesUntil(snapshotLevelToConvert.Value);
            }
            else if (persistedToPersist is not null)
            {
                using PersistedSnapshot _ = persistedToPersist;
                PersistPersistedSnapshot(persistedToPersist);
                _currentPersistedStateId = persistedToPersist.To;
                int pruned = _persistedSnapshotRepository.PruneBefore(persistedToPersist.To);
                if (pruned > 0)
                {
                    Metrics.PersistedSnapshotPrunes += pruned;
                    Metrics.PersistedSnapshotCount = _persistedSnapshotRepository.SnapshotCount;
                    Metrics.PersistedSnapshotMemory = _persistedSnapshotRepository.BaseSnapshotMemory;
                    Metrics.CompactedPersistedSnapshotMemory = _persistedSnapshotRepository.CompactedSnapshotMemory;
                    if (_logger.IsDebug) _logger.Debug($"Pruned {pruned} persisted snapshots before block {persistedToPersist.To.BlockNumber}");
                }
            }
            else
            {
                break;
            }
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
        StateId? latestStateId = _snapshotRepository.GetLastSnapshotId();

        if (latestStateId is null)
        {
            return currentPersistedState;
        }

        // Persist all snapshots from current persisted state to latest
        while (currentPersistedState.BlockNumber < latestStateId.Value.BlockNumber)
        {
            // Try finalized snapshots first (compacted, then non-compacted)
            (PersistedSnapshot? persisted, Snapshot? snapshotToPersist) = GetFinalizedSnapshotAtBlockNumber(
                currentPersistedState.BlockNumber + _compactSize,
                currentPersistedState,
                compactedSnapshot: true);
            persisted?.Dispose();

            if (snapshotToPersist is null)
            {
                (persisted, snapshotToPersist) = GetFinalizedSnapshotAtBlockNumber(
                    currentPersistedState.BlockNumber + 1,
                    currentPersistedState,
                    compactedSnapshot: false);
                persisted?.Dispose();
            }

            // Fall back to the first available snapshot if finalized not available
            snapshotToPersist ??= GetFirstSnapshotAtBlockNumber(
                currentPersistedState.BlockNumber + _compactSize,
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
            PersistSnapshot(snapshotToPersist);
            _currentPersistedStateId = snapshotToPersist.To;
            currentPersistedState = _currentPersistedStateId;
        }

        return currentPersistedState;
    }

    internal void PersistSnapshot(Snapshot snapshot)
    {
        long compactLength = snapshot.To.BlockNumber! - snapshot.From.BlockNumber!;

        // Usually at the start of the application
        if (compactLength != _compactSize && _logger.IsTrace) _logger.Trace($"Persisting non compacted state of length {compactLength}");

        long sw = Stopwatch.GetTimestamp();
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To))
        {
            foreach (KeyValuePair<AddressAsKey, bool> toSelfDestructStorage in snapshot.SelfDestructedStorageAddresses)
            {
                if (toSelfDestructStorage.Value)
                {
                    continue;
                }

                batch.SelfDestruct(toSelfDestructStorage.Key.Value);
            }

            foreach (KeyValuePair<AddressAsKey, Account?> kv in snapshot.Accounts)
            {
                (AddressAsKey addr, Account? account) = kv;
                batch.SetAccount(addr, account);
            }

            foreach (KeyValuePair<(AddressAsKey, UInt256), SlotValue?> kv in snapshot.Storages)
            {
                ((Address addr, UInt256 slot), SlotValue? value) = kv;

                batch.SetStorage(addr, slot, value);
            }

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StateNodeKeys.Select<TreePath, (Hash256AsKey, TreePath)>((path) => (new Hash256AsKey(Hash256.Zero), path)));
            _trieNodesSortBuffer.Sort();

            long stateNodesSize = 0;
            // foreach (var tn in snapshot.TrieNodes)
            foreach ((Hash256AsKey, TreePath) k in _trieNodesSortBuffer)
            {
                (_, TreePath path) = k;

                snapshot.TryGetStateNode(path, out TrieNode? node);

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
                batch.SetStateTrieNode(path, node);

                node.IsPersisted = true;
            }

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StorageTrieNodeKeys);
            _trieNodesSortBuffer.Sort();

            long storageNodesSize = 0;
            // foreach (var tn in snapshot.TrieNodes)
            foreach ((Hash256AsKey, TreePath) k in _trieNodesSortBuffer)
            {
                (Hash256AsKey address, TreePath path) = k;

                snapshot.TryGetStorageNode(address, path, out TrieNode? node);

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
                batch.SetStorageTrieNode(address, path, node);

                node.IsPersisted = true;
            }

            Metrics.FlatPersistenceSnapshotSize.Observe(stateNodesSize, labels: new StringLabel("state_nodes"));
            Metrics.FlatPersistenceSnapshotSize.Observe(storageNodesSize, labels: new StringLabel("storage_nodes"));
        }

        Metrics.FlatPersistenceTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

    private PersistedSnapshot? TryGetForcePersistedSnapshot(StateId currentPersistedState, long totalDepth)
    {
        if (totalDepth <= _longFinalityReorgDepth) return null;
        PersistedSnapshot? oldest = _persistedSnapshotRepository.TryGetSnapshotFrom(currentPersistedState);
        if (oldest is not null && _logger.IsWarn)
            _logger.Warn($"Total reorg depth {totalDepth} exceeds LongFinalityReorgDepth {_longFinalityReorgDepth}. Force persisting persisted snapshot {oldest.From} -> {oldest.To}.");
        return oldest;
    }

    internal void PersistPersistedSnapshot(PersistedSnapshot snapshot)
    {
        long sw = Stopwatch.GetTimestamp();

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To))
        {
            foreach (KeyValuePair<AddressAsKey, bool> kv in snapshot.SelfDestructedStorageAddresses)
            {
                if (kv.Value) continue;
                batch.SelfDestruct(kv.Key);
            }

            foreach (KeyValuePair<AddressAsKey, Account?> kv in snapshot.Accounts)
            {
                batch.SetAccount(kv.Key, kv.Value);
            }

            foreach (KeyValuePair<(AddressAsKey, UInt256), SlotValue?> kv in snapshot.Storages)
            {
                ((Address addr, UInt256 slot), SlotValue? value) = kv;
                batch.SetStorage(addr, slot, value);
            }

            foreach (KeyValuePair<TreePath, TrieNode> kv in snapshot.StateNodes)
            {
                batch.SetStateTrieNode(kv.Key, kv.Value);
            }

            foreach (KeyValuePair<(Hash256AsKey, TreePath), TrieNode> kv in snapshot.StorageNodes)
            {
                ((Hash256AsKey address, TreePath path), TrieNode node) = kv;
                batch.SetStorageTrieNode(address, path, node);
            }
        }

        Metrics.FlatPersistenceTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

}
