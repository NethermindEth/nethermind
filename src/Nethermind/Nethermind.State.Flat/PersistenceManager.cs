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
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;

[assembly: InternalsVisibleTo("Nethermind.State.Flat.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.State.Flat;

public class PersistenceManager(
    IFlatDbConfig configuration,
    IFinalizedStateProvider finalizedStateProvider,
    IPersistence persistence,
    ISnapshotRepository snapshotRepository,
    ILogManager logManager,
    PersistedSnapshotCompactors persistedSnapshotCompactors,
    PersistedSnapshotRepositories persistedSnapshotRepositories) : IPersistenceManager
{
    private readonly ILogger _logger = logManager.GetClassLogger<PersistenceManager>();
    private readonly int _minReorgDepth = configuration.MinReorgDepth;
    private readonly int _maxInMemoryReorgDepth = configuration.MaxInMemoryReorgDepth;
    private readonly int _longFinalityReorgDepth = configuration.LongFinalityReorgDepth;
    private readonly int _compactSize = configuration.CompactSize;
    private readonly IPersistence _persistence = persistence;
    private readonly ISnapshotRepository _snapshotRepository = snapshotRepository;
    private readonly IFinalizedStateProvider _finalizedStateProvider = finalizedStateProvider;
    private readonly IPersistedSnapshotCompactor _smallCompactor = persistedSnapshotCompactors.Small;
    private readonly IPersistedSnapshotCompactor _largeCompactor = persistedSnapshotCompactors.Large;
    private readonly IPersistedSnapshotRepository _smallRepo = persistedSnapshotRepositories.Small;
    private readonly IPersistedSnapshotRepository _largeRepo = persistedSnapshotRepositories.Large;
    private readonly List<(Hash256, TreePath)> _trieNodesSortBuffer = []; // Presort make it faster
    private readonly Lock _persistenceLock = new();

    private readonly Channel<ArrayPoolList<StateId>> _compactPersistedJobs = Channel.CreateBounded<ArrayPoolList<StateId>>(16);
    private readonly Channel<StateId> _boundaryCompactJobs = Channel.CreateBounded<StateId>(16);
    private readonly CancellationTokenSource _cancelTokenSource = new();
    private Task? _compactPersistedTask;
    private Task[]? _boundaryCompactorTasks;

    private const int BoundaryCompactorWorkerCount = 4;

    private StateId _currentPersistedStateId = StateId.PreGenesis;

    private Task EnsureCompactorStarted()
    {
        _compactPersistedTask ??= RunPersistedCompactor(_cancelTokenSource.Token);
        if (_boundaryCompactorTasks is null)
        {
            Task[] tasks = new Task[BoundaryCompactorWorkerCount];
            for (int i = 0; i < BoundaryCompactorWorkerCount; i++)
                tasks[i] = RunBoundaryCompactor(_cancelTokenSource.Token);
            _boundaryCompactorTasks = tasks;
        }
        return _compactPersistedTask;
    }

    private readonly Histogram _persistedSnapshotConvertTime =
        Prometheus.Metrics.CreateHistogram("persisted_snapshot_convert_time", "persisted_snapshot_convert_time", "size");

    private async Task RunPersistedCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ArrayPoolList<StateId> batch in _compactPersistedJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    ProcessCompactBatch(batch);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error compacting persisted snapshot batch. {ex}");
                }
                finally
                {
                    batch.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            while (_compactPersistedJobs.Reader.TryRead(out ArrayPoolList<StateId>? batch))
                batch.Dispose();
        }
    }

    private void ProcessCompactBatch(ArrayPoolList<StateId> batch)
    {
        if (batch.Count == 0) return;

        // Offload boundary states (block divisible by _compactSize — heaviest merges) to the
        // parallel boundary channel so the next batch can start before these compactions finish.
        using ArrayPoolList<StateId> boundaries = new(batch.Count);
        SortedDictionary<int, List<StateId>> buckets = new();
        for (int i = 0; i < batch.Count; i++)
        {
            StateId s = batch[i];
            long b = s.BlockNumber;
            if (b == 0) continue;

            if (b % _compactSize == 0)
            {
                boundaries.Add(s);
                continue;
            }

            // Non-boundary: lowest-set-bit alignment is strictly < _compactSize.
            int compactSize = (int)(b & -b);
            if (!buckets.TryGetValue(compactSize, out List<StateId>? bucket))
                buckets[compactSize] = bucket = [];
            bucket.Add(s);
        }

        // Non-boundary states live only in the small repo (see AddToPersistence:
        // _smallRepo.ConvertSnapshotToPersistedSnapshot for non-boundary blocks).
        foreach (KeyValuePair<int, List<StateId>> kv in buckets)
            Parallel.ForEach(kv.Value, state => _smallCompactor.DoCompactSnapshot(state));

        foreach (StateId boundary in boundaries)
            _boundaryCompactJobs.Writer.WriteAsync(boundary).AsTask().Wait();
    }

    private async Task RunBoundaryCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateId state in _boundaryCompactJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // Boundary snapshots always live in the large repo (see AddToPersistence:
                    // _largeRepo.ConvertSnapshotToPersistedSnapshot at the boundary block).
                    _largeCompactor.DoCompactSnapshot(state);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error compacting boundary persisted snapshot {state}. {ex}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancelTokenSource.Cancel();
        _compactPersistedJobs.Writer.Complete();
        _boundaryCompactJobs.Writer.Complete();
        if (_compactPersistedTask is not null)
            await _compactPersistedTask;
        if (_boundaryCompactorTasks is not null)
            await Task.WhenAll(_boundaryCompactorTasks);
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
            StateId targetStateId = new(blockNumber, finalizedStateRoot);
            bool found = compactedSnapshot
                ? _largeRepo.TryLeaseSnapshotTo(targetStateId, out PersistedSnapshot? persisted)
                : _smallRepo.TryLeaseSnapshotTo(targetStateId, out persisted);
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

        long? TryGetSnapshotLevelToConvert() => _snapshotRepository.GetEarliestSnapshotId()?.BlockNumber;

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

            // Memory pressure with unfinalized state: convert to persisted snapshots instead of force-persisting to RocksDB.
            // Mirror the ShallowDepth floor: never convert unless the in-memory window is wider than
            // _maxInMemoryReorgDepth + _compactSize, otherwise we end up persisting (and removing from memory)
            // the freshest snapshot before its parent edges exist on disk — producing gaps in Persisted.Base on restart.
            long? earliestInMemoryUnf = TryGetSnapshotLevelToConvert();
            if (earliestInMemoryUnf == null)
            {
                return (null, null, null);
            }

            long inMemoryDepthUnf = lastSnapshotNumber - earliestInMemoryUnf.Value;
            if (inMemoryDepthUnf <= _maxInMemoryReorgDepth + _compactSize)
            {
                return (null, null, null);
            }

            if (_logger.IsWarn) _logger.Warn($"Very long unfinalized state. Converting to persisted snapshots. finalized block number is {finalizedBlockNumber}.");

            return (null, null, earliestInMemoryUnf);
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
                long start = snapshotLevelToConvert.Value;
                // Next compactSize-aligned boundary >= start
                long end = ((start - 1) / _compactSize + 1) * _compactSize;

                ArrayPoolList<StateId> allStateIds = new(64);
                int boundaryStart = 0;
                for (long b = start; b <= end; b++)
                {
                    if (b == end) boundaryStart = allStateIds.Count;
                    using ArrayPoolList<StateId> statesAtBlock = _snapshotRepository.GetStatesAtBlockNumber(b);
                    foreach (StateId state in statesAtBlock)
                        allStateIds.Add(state);
                }

                // Parallel base conversion across the whole batch
                Parallel.ForEach(allStateIds, state =>
                {
                    if (_snapshotRepository.TryLeaseState(state, out Snapshot? snapshot))
                    {
                        long sw = Stopwatch.GetTimestamp();
                        _smallRepo.ConvertSnapshotToPersistedSnapshot(snapshot);
                        _persistedSnapshotConvertTime.WithLabels("base").Observe(Stopwatch.GetTimestamp() - sw);
                        snapshot.Dispose();
                    }
                });

                // Boundary-block compacted promotion (sequential; full-size compacted only exists at end)
                for (int i = boundaryStart; i < allStateIds.Count; i++)
                {
                    StateId endState = allStateIds[i];
                    if (_snapshotRepository.TryLeaseCompactedState(endState, out Snapshot? compacted))
                    {
                        if (compacted.To.BlockNumber - compacted.From.BlockNumber == _compactSize)
                        {
                            long sw = Stopwatch.GetTimestamp();
                            _largeRepo.ConvertSnapshotToPersistedSnapshot(compacted);
                            _persistedSnapshotConvertTime.WithLabels("full32").Observe(Stopwatch.GetTimestamp() - sw);
                        }
                        compacted.Dispose();
                    }
                }

                EnsureCompactorStarted();
                _compactPersistedJobs.Writer.WriteAsync(allStateIds).AsTask().Wait();

                _snapshotRepository.RemoveStatesUntil(end);
            }
            else if (persistedToPersist is not null)
            {
                using PersistedSnapshot _ = persistedToPersist;
                PersistPersistedSnapshot(persistedToPersist);
                _currentPersistedStateId = persistedToPersist.To;
                int pruned = _smallRepo.PruneBefore(persistedToPersist.To) + _largeRepo.PruneBefore(persistedToPersist.To);
                if (pruned > 0)
                {
                    Metrics.PersistedSnapshotPrunes += pruned;
                    Metrics.PersistedSnapshotCount = _smallRepo.SnapshotCount + _largeRepo.SnapshotCount;
                    Metrics.PersistedSnapshotMemory = _smallRepo.BaseSnapshotMemory + _largeRepo.BaseSnapshotMemory;
                    Metrics.CompactedPersistedSnapshotMemory = _smallRepo.CompactedSnapshotMemory + _largeRepo.CompactedSnapshotMemory;
                    Metrics.ArenaFileCount = _smallRepo.ArenaFileCount + _largeRepo.ArenaFileCount;
                    Metrics.ArenaMappedBytes = _smallRepo.ArenaMappedBytes + _largeRepo.ArenaMappedBytes;
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

    public void ResetPersistedStateId()
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        _currentPersistedStateId = reader.CurrentState;
    }

    internal void PersistSnapshot(Snapshot snapshot)
    {
        long compactLength = snapshot.To.BlockNumber! - snapshot.From.BlockNumber!;

        // Usually at the start of the application
        if (compactLength != _compactSize && _logger.IsTrace) _logger.Trace($"Persisting non compacted state of length {compactLength}");

        long sw = Stopwatch.GetTimestamp();
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To))
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
            // foreach (var tn in snapshot.TrieNodes)
            foreach ((Hash256, TreePath) k in _trieNodesSortBuffer.Select(v => ((Hash256, TreePath))v))
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

                ReadOnlySpan<byte> rlp = node.FullRlp.AsSpan();
                stateNodesSize += rlp.Length;
                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetStateTrieNode(path, rlp);

                node.IsPersisted = true;
            }

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StorageTrieNodeKeys);
            _trieNodesSortBuffer.Sort();

            long storageNodesSize = 0;
            // foreach (var tn in snapshot.TrieNodes)
            foreach ((Hash256, TreePath) k in _trieNodesSortBuffer.Select(v => ((Hash256, TreePath))v))
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

                ReadOnlySpan<byte> rlp = node.FullRlp.AsSpan();
                storageNodesSize += rlp.Length;
                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetStorageTrieNode(address, path, rlp);
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
        // Large tier first (longer ranges = faster catch-up); fall back to small.
        PersistedSnapshot? oldest = _largeRepo.TryGetSnapshotFrom(currentPersistedState)
                                    ?? _smallRepo.TryGetSnapshotFrom(currentPersistedState);
        if (oldest is not null && _logger.IsWarn)
            _logger.Warn($"Total reorg depth {totalDepth} exceeds LongFinalityReorgDepth {_longFinalityReorgDepth}. Force persisting persisted snapshot {oldest.From} -> {oldest.To}.");
        return oldest;
    }

    internal void PersistPersistedSnapshot(PersistedSnapshot snapshot)
    {
        long sw = Stopwatch.GetTimestamp();

        using WholeReadSession session = snapshot.BeginWholeReadSession();
        PersistedSnapshotScanner scanner = new(session, snapshot);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To))
        {
            foreach (PersistedSnapshotScanner.SelfDestructEntry entry in scanner.SelfDestructedStorageAddresses)
            {
                if (entry.IsNew) continue;
                // PersistedSnapshot only stores the 20-byte address-hash prefix as the
                // column 0x01 key — the original Address is unrecoverable. Use the hash-
                // keyed batch entrypoint, which is what the underlying flat layer uses
                // anyway (Address-keyed methods just hash internally).
                batch.SelfDestructRaw(entry.AddressHash);
            }

            foreach (PersistedSnapshotScanner.AccountEntry entry in scanner.Accounts)
            {
                if (entry.Account is { } account)
                    batch.SetAccountRaw(entry.AddressHash, account);
                else
                    batch.RemoveAccountRaw(entry.AddressHash);
            }

            foreach (PersistedSnapshotScanner.StorageEntry entry in scanner.Storages)
            {
                ValueHash256 slotHash = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(entry.Slot, ref slotHash);
                batch.SetStorageRaw(entry.AddressHash, slotHash, entry.Value);
            }

            foreach (PersistedSnapshotScanner.StateNodeEntry entry in scanner.StateNodes)
                batch.SetStateTrieNode(entry.Path, entry.Rlp);

            foreach (PersistedSnapshotScanner.StorageNodeEntry entry in scanner.StorageNodes)
                batch.SetStorageTrieNode(entry.AddressHash.ToCommitment(), entry.Path, entry.Rlp);
        }

        Metrics.FlatPersistenceTime.Observe(Stopwatch.GetTimestamp() - sw);
    }

}
