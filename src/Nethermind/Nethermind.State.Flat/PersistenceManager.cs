// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;

[assembly: InternalsVisibleTo("Nethermind.State.Flat.Test")]

namespace Nethermind.State.Flat;

public class PersistenceManager: IAsyncDisposable
{
    private readonly ILogger _logger;
    private static readonly Histogram _flatdiffimes = FlatDbManager._flatdiffimes;
    private readonly Histogram _writesSize = DevMetric.Factory.CreateHistogram("persistence_manager_writes", "writes", new HistogramConfiguration()
    {
        LabelNames = ["payload"],
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 8, 10)
    });
    private readonly int _minimumPruningBoundary;
    private readonly int _forcedPruningBoundary;
    private readonly int _compactSize;
    private readonly IFinalizedStateProvider _finalizedStateProvider;
    private readonly List<(Hash256AsKey, TreePath)> _trieNodesSortBuffer = new List<(Hash256AsKey, TreePath)>(); // Presort make it faster

    // Readers are created a lot. So we put it behind a refcounted wrapper.
    // However, it must not stay for too long as open reader prevent database compaction.
    private RefCountingPersistenceReader? _cachedReader = null;
    private readonly Lock _readerCacheLock = new Lock();
    private readonly Task _clearReaderTask;
    private bool _mustNotClearReaderCache = false;
    private StateId _currentPersistedStateId = StateId.PreGenesis;
    private readonly CancellationTokenSource _cancelTokenSource;
    private int _isDisposed = 0;

    private readonly IPersistence _persistence;
    private readonly ISnapshotRepository _snapshotRepository;

    public PersistenceManager(
        IFlatDbConfig configuration,
        IFinalizedStateProvider finalizedStateProvider,
        IPersistence persistence,
        ISnapshotRepository snapshotRepository,
        IProcessExitSource exitSource,
        ILogManager logManager)
    {
        _finalizedStateProvider = finalizedStateProvider;
        _persistence = persistence;
        _snapshotRepository = snapshotRepository;
        _logger = logManager.GetClassLogger();
        _forcedPruningBoundary = configuration.MaxPruningBoundary;
        _minimumPruningBoundary = configuration.PruningBoundary;
        _compactSize = configuration.CompactSize;

        _cancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(exitSource.Token);

        _clearReaderTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            try
            {
                while (true)
                {
                    await timer.WaitForNextTickAsync(_cancelTokenSource.Token);

                    ClearReaderCache();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        using var _ = LeaseReader();
    }

    public IPersistence.IPersistenceReader LeaseReader()
    {
        var cachedReader = _cachedReader;
        if (cachedReader != null && cachedReader.TryAcquire())
        {
            return cachedReader;
        }

        using var _ = _readerCacheLock.EnterScope();
        return LeaseReaderNoLock();
    }

    private IPersistence.IPersistenceReader LeaseReaderNoLock()
    {
        var cachedReader = _cachedReader;
        while (true)
        {
            cachedReader = _cachedReader;
            if (cachedReader is null)
            {
                _cachedReader = cachedReader = new RefCountingPersistenceReader(
                    _persistence.CreateReader(),
                    _logger
                );
                _currentPersistedStateId = cachedReader.CurrentState;
            }

            if (cachedReader.TryAcquire())
            {
                return cachedReader;
            }
            else
            {
                // Was disposed but not cleared. Not yet at least.
                Interlocked.CompareExchange(ref _cachedReader, null, cachedReader);
            }
        }
    }

    /// <summary>
    /// Prevent reader cache from being disposed. So often we use database that is not consistent, as in
    /// using multiple separate database. But that means that the reader must not be created during persistence as the
    /// database are not consistent with each other. So we prime the reader here and prevent dispose until the persistence
    /// is completed.
    /// </summary>
    /// <returns></returns>
    private KeepReaderLock EnterReaderLockScope()
    {
        IPersistence.IPersistenceReader reader;
        using (var _ = _readerCacheLock.EnterScope())
        {
            _mustNotClearReaderCache = true;
            reader = LeaseReaderNoLock();
        }
        using var _r = reader;  // Dispose

        return new KeepReaderLock(this);
    }

    private ref struct KeepReaderLock(PersistenceManager manager): IDisposable
    {
        public void Dispose()
        {
            manager._mustNotClearReaderCache = false;
            manager.ClearReaderCache();
        }
    }

    private void ClearReaderCache()
    {
        using var _ = _readerCacheLock.EnterScope();
        if (_mustNotClearReaderCache) return;
        RefCountingPersistenceReader? cachedReader = _cachedReader;
        _cachedReader = null;
        cachedReader?.Dispose();
    }

    internal StateId GetCurrentPersistedStateId()
    {
        return _currentPersistedStateId;
    }

    private Snapshot? GetFinalizedSnapshotAtBlockNumber(long blockNumber, StateId currentPersistedState, bool compactedSnapshot)
    {
        Hash256? finalizedStateRoot = _finalizedStateProvider.GetFinalizedStateRootAt(blockNumber);
        using ArrayPoolList<StateId> compactedStates = _snapshotRepository.GetStatesAtBlockNumber(blockNumber);

        foreach (var stateId in compactedStates)
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

                return snapshot;
            }

            snapshot.Dispose();
        }

        return null;
    }

    private Snapshot? GetFirstSnapshotAtBlockNumber(long blockNumber, StateId currentPersistedState, bool compactedSnapshot)
    {
        using ArrayPoolList<StateId> nextBlockStates = _snapshotRepository.GetStatesAtBlockNumber(blockNumber);

        foreach (var stateId in nextBlockStates)
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

    internal Snapshot? DetermineSnapshotToPersist(StateId latestSnapshot)
    {
        // Actually the latest compacted snapshot, not the latest snapshot.
        long lastSnapshotNumber = latestSnapshot.BlockNumber;

        StateId currentPersistedState = GetCurrentPersistedStateId();
        long finalizedBlockNumber = _finalizedStateProvider.FinalizedBlockNumber;
        long inMemoryStateDepth = lastSnapshotNumber - currentPersistedState.BlockNumber;
        long afterPersistStateDepth = inMemoryStateDepth - _compactSize;
        if (afterPersistStateDepth < _minimumPruningBoundary)
        {
            // Keep some state in memory
            return null;
        }

        Snapshot? snapshotToPersist;

        long afterPersistPersistedBlockNumber = currentPersistedState.BlockNumber + _compactSize;
        if (afterPersistPersistedBlockNumber > finalizedBlockNumber)
        {
            // Unfinalized
            if (inMemoryStateDepth <= _forcedPruningBoundary)
            {
                return null;
            }

            _logger.Warn($"Very long unfinalized state. Force persisting to conserve memory. finalized block number is {finalizedBlockNumber}.");
            snapshotToPersist = GetFirstSnapshotAtBlockNumber(currentPersistedState.BlockNumber + _compactSize, currentPersistedState, true) ??
                                GetFirstSnapshotAtBlockNumber(currentPersistedState.BlockNumber + 1, currentPersistedState, false);
        }
        else
        {
            snapshotToPersist = GetFinalizedSnapshotAtBlockNumber(currentPersistedState.BlockNumber + _compactSize, currentPersistedState, true) ??
                                GetFinalizedSnapshotAtBlockNumber(currentPersistedState.BlockNumber + 1, currentPersistedState, false);
        }

        if (snapshotToPersist is null)
        {
            _logger.Warn($"Unable to find snapshot to persist. Current persisted state {currentPersistedState}. Compact size {_compactSize}.");
        }

        return snapshotToPersist;
    }

    internal void AddToPersistence(StateId latestSnapshot)
    {
        // Attempt to add snapshots into bigcache
        while (true)
        {
            long sw = Stopwatch.GetTimestamp();

            Snapshot? snapshotToSave = DetermineSnapshotToPersist(latestSnapshot);
            _flatdiffimes.WithLabels("add_to_persistence", "state_picked").Observe(Stopwatch.GetTimestamp() - sw);

            if (snapshotToSave is null) return;
            using var _ = snapshotToSave; // dispose

            // Add the canon snapshot
            PersistSnapshot(snapshotToSave);
            _currentPersistedStateId = snapshotToSave.To;

            _flatdiffimes.WithLabels("add_to_persistence", "persist").Observe(Stopwatch.GetTimestamp() - sw);
        }
    }

    internal void PersistSnapshot(Snapshot snapshot)
    {
        long compactLength = snapshot.To.BlockNumber! - snapshot.From.BlockNumber!;
        if (compactLength != _compactSize)
        {
            _logger.Warn($"ccompact length is {compactLength}");
        }

        using var _rl = EnterReaderLockScope();

        long sw = Stopwatch.GetTimestamp();
        using (var batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To))
        {
            _flatdiffimes.WithLabels("persistence", "start_batch").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();
            int counter = 0;
            foreach (var toSelfDestructStorage in snapshot.SelfDestructedStorageAddresses)
            {
                if (toSelfDestructStorage.Value)
                {
                    continue;
                }

                int num = batch.SelfDestruct(toSelfDestructStorage.Key.Value);
                counter++;
            }
            _flatdiffimes.WithLabels("persistence", "self_destruct").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            foreach (var kv in snapshot.Accounts)
            {
                (AddressAsKey addr, Account? account) = kv;
                batch.SetAccount(addr, account);
            }
            _flatdiffimes.WithLabels("persistence", "accounts").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            foreach (var kv in snapshot.Storages)
            {
                ((Address addr, UInt256 slot), SlotValue? value) = kv;

                batch.SetStorage(addr, slot, value);
            }
            _flatdiffimes.WithLabels("persistence", "storages").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StateNodeKeys.Select<TreePath, (Hash256AsKey, TreePath)>((path) => (new Hash256AsKey(Hash256.Zero), path)));
            _trieNodesSortBuffer.Sort();
            _flatdiffimes.WithLabels("persistence", "trienode_sort_state").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            long stateNodesSize = 0;
            // foreach (var tn in snapshot.TrieNodes)
            foreach (var k in _trieNodesSortBuffer)
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
            _flatdiffimes.WithLabels("persistence", "trienodes").Observe(Stopwatch.GetTimestamp() - sw);

            sw = Stopwatch.GetTimestamp();
            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StorageTrieNodeKeys);
            _trieNodesSortBuffer.Sort();
            _flatdiffimes.WithLabels("persistence", "trienode_sort").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            long storageNodesSize = 0;
            // foreach (var tn in snapshot.TrieNodes)
            foreach (var k in _trieNodesSortBuffer)
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
            _flatdiffimes.WithLabels("persistence", "trienodes").Observe(Stopwatch.GetTimestamp() - sw);

            sw = Stopwatch.GetTimestamp();

            _writesSize.WithLabels("state_nodes").Observe(stateNodesSize);
            _writesSize.WithLabels("storage_nodes").Observe(storageNodesSize);
        }
        _flatdiffimes.WithLabels("persistence", "dispose").Observe(Stopwatch.GetTimestamp() - sw);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 1) return;

        _cancelTokenSource.Cancel();
        await _clearReaderTask;
        _cachedReader?.Dispose();
        _cancelTokenSource.Dispose();
    }
}
