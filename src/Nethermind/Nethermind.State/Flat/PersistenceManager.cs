// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;

namespace Nethermind.State.Flat;

public class PersistenceManager: IAsyncDisposable
{
    private readonly FlatDiffRepository _flatDiffRepository;
    private readonly ILogger _logger;
    private readonly Histogram _flatdiffimes;
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

    private readonly IPersistence _persistence;

    public PersistenceManager(
        FlatDiffRepository flatDiffRepository,
        FlatDiffRepository.Configuration configuration,
        IFinalizedStateProvider finalizedStateProvider,
        IPersistence persistence,
        IProcessExitSource exitSource,
        ILogManager logManager)
    {
        _flatDiffRepository = flatDiffRepository;
        _finalizedStateProvider = finalizedStateProvider;
        _persistence = persistence;
        _logger = logManager.GetClassLogger();
        _flatdiffimes = FlatDiffRepository._flatdiffimes;
        _forcedPruningBoundary = configuration.ForcedPruningBoundary;
        _minimumPruningBoundary = configuration.Boundary;
        _compactSize = configuration.CompactSize;

        _clearReaderTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            var cancellation = exitSource.Token;

            try
            {
                while (true)
                {
                    await timer.WaitForNextTickAsync(cancellation);

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

    internal void AddToPersistence()
    {
        // Attempt to add snapshots into bigcache
        while (true)
        {
            Snapshot? snapshotToSave = null;
            long sw = Stopwatch.GetTimestamp();
            _flatdiffimes.WithLabels("add_to_persistence", "repolock").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();
            long lastSnapshotNumber = _flatDiffRepository.GetLastSnapshotId()?.blockNumber ?? 0;
            StateId currentPersistedState = GetCurrentPersistedStateId();
            long finalizedBlockNumber = _finalizedStateProvider.FinalizedBlockNumber;

            bool forcedFinalizedState = false;
            long inMemoryStateDepth = lastSnapshotNumber - currentPersistedState.blockNumber;

            if (currentPersistedState.blockNumber + _compactSize > finalizedBlockNumber)
            {
                // Unfinalized
                if (inMemoryStateDepth > _forcedPruningBoundary)
                {
                    _logger.Warn($"Very long unfinalized state. Force persisting to conserve memory. finalized block number is {finalizedBlockNumber}.");
                    forcedFinalizedState = true;
                }
                else
                {
                    break;
                }
            }
            else if (inMemoryStateDepth + _compactSize < _minimumPruningBoundary) // add compact size as that will be removed after persistence
            {
                // Keep some state in memory
                break;
            }


            if (!forcedFinalizedState)
            {
                Hash256 finalizedStateRootAtCompacted = _finalizedStateProvider.GetFinalizedStateRootAt(currentPersistedState.blockNumber + _compactSize);
                using ArrayPoolList<StateId> compactedStates = _flatDiffRepository.GetStatesAtBlockNumber(currentPersistedState.blockNumber + _compactSize);

                // Note: Need to verify that this is finalized
                foreach (var stateId in compactedStates)
                {
                    if (stateId.stateRoot == finalizedStateRootAtCompacted)
                    {
                        if (_flatDiffRepository.TryLeaseCompactedState(stateId, out var compactedState))
                        {
                            if (compactedState.From == currentPersistedState)
                            {
                                if (_logger.IsDebug) _logger.Debug($"Persisting compacted state {stateId}");

                                snapshotToSave = compactedState;
                                break;
                            }
                            else
                            {
                                compactedState.Dispose();
                            }
                        }
                    }
                }

                // Note: This assume there is always a snapshot right next to current persisted state.
                if (snapshotToSave is null)
                {
                    Hash256 finalizedStateRootAtNextBlock = _finalizedStateProvider.GetFinalizedStateRootAt(currentPersistedState.blockNumber + 1);
                    using ArrayPoolList<StateId> nextBlockStates = _flatDiffRepository.GetStatesAtBlockNumber(currentPersistedState.blockNumber + 1);

                    foreach (var stateId in nextBlockStates)
                    {
                        if (stateId.stateRoot == finalizedStateRootAtNextBlock)
                        {
                            if (_flatDiffRepository.TryLeaseState(stateId, out var snapshot))
                            {
                                if (snapshot.From == currentPersistedState)
                                {
                                    if (_logger.IsDebug) _logger.Debug($"Persisting uncompacted state {stateId}");

                                    snapshotToSave = snapshot;
                                    break;
                                }
                                else
                                {
                                    snapshot.Dispose();
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                using ArrayPoolList<StateId> nextBlockStates = _flatDiffRepository.GetStatesAtBlockNumber(currentPersistedState.blockNumber + _compactSize);

                // Just pick the first one
                foreach (var stateId in nextBlockStates)
                {
                    if (_flatDiffRepository.TryLeaseCompactedState(stateId, out var snapshot))
                    {
                        if (snapshot.From == currentPersistedState)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Force persisting state {stateId}");

                            snapshotToSave = snapshot;
                            break;
                        }
                        else
                        {
                            snapshot.Dispose();
                        }
                    }
                }

                if (snapshotToSave is null)
                {
                    nextBlockStates.Clear();
                    nextBlockStates.AddRange(_flatDiffRepository.GetStatesAtBlockNumber(currentPersistedState.blockNumber + 1));

                    foreach (var stateId in nextBlockStates)
                    {
                        if (_flatDiffRepository.TryLeaseState(stateId, out var snapshot))
                        {
                            if (snapshot.From == currentPersistedState)
                            {
                                if (_logger.IsWarn) _logger.Warn($"Force persisting state {stateId}");

                                snapshotToSave = snapshot;
                                break;
                            }
                            else
                            {
                                snapshot.Dispose();
                            }
                        }
                    }
                }
            }

            _flatdiffimes.WithLabels("add_to_persistence", "state_picked").Observe(Stopwatch.GetTimestamp() - sw);

            if (snapshotToSave is null)
            {
                // So very bad here. The node will keep taking up state until it ran out of memory.
                if (_logger.IsWarn) _logger.Warn($"Unable to determine state to persist.");
                return;
            }

            // Add the canon snapshot
            PersistSnapshot(snapshotToSave);
            snapshotToSave.Dispose();
            _currentPersistedStateId = snapshotToSave.To;

            sw = Stopwatch.GetTimestamp();

            // And we remove it
            // Note: Before this point, new reader must show new consistent view.
            _flatDiffRepository.OnStatePersisted(snapshotToSave.To);
            _flatdiffimes.WithLabels("add_to_persistence", "cleanup").Observe(Stopwatch.GetTimestamp() - sw);

            sw = Stopwatch.GetTimestamp();

            _flatdiffimes.WithLabels("add_to_persistence", "reorg_boundary").Observe(Stopwatch.GetTimestamp() - sw);
        }
    }

    private void PersistSnapshot(Snapshot snapshot)
    {
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
                    /*
                    int deleted = batch.SelfDestruct(toSelfDestructStorage.Key.Value);
                    if (toSelfDestructStorage.Key.Value == FlatWorldStateScope.DebugAddress)
                    {
                        Console.Error.WriteLine($"Selfdestruct should skip {toSelfDestructStorage.Key}");
                    }
                    if (deleted > 0)
                    {
                        _logger.Warn($"Should selfdestruct {toSelfDestructStorage.Key}. Deleted {deleted}. Snapshot range {snapshot.From} {snapshot.To}");
                        throw new Exception($"Should sefl destruct not called properly {toSelfDestructStorage.Key}");
                    }
                    */
                    continue;
                }

                int num = batch.SelfDestruct(toSelfDestructStorage.Key.Value);
                counter++;
            }
            _flatdiffimes.WithLabels("persistence", "self_destruct").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            foreach (var kv in snapshot.Accounts)
            {
                (Address addr, Account? account) = kv;
                if (account is null)
                    batch.RemoveAccount(addr);
                else
                    batch.SetAccount(addr, account);
            }
            _flatdiffimes.WithLabels("persistence", "accounts").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            foreach (var kv in snapshot.Storages)
            {
                ((Address addr, UInt256 slot), byte[] value) = kv;

                if (value is null || Bytes.AreEqual(value, StorageTree.ZeroBytes))
                {
                    batch.RemoveStorage(addr, slot);
                }
                else
                {
                    batch.SetStorage(addr, slot, value);
                }
            }
            _flatdiffimes.WithLabels("persistence", "storages").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StateNodeKeys.Select<TreePath, (Hash256AsKey, TreePath)>((path) => (null, path)));
            _trieNodesSortBuffer.Sort();
            _flatdiffimes.WithLabels("persistence", "trienode_sort_state").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            // foreach (var tn in snapshot.TrieNodes)
            foreach (var k in _trieNodesSortBuffer)
            {
                (_, TreePath path) = k;

                snapshot.TryGetStateNode(path, out TrieNode node);

                if (node.FullRlp.Length == 0)
                {
                    // TODO: Need to double check this case. Does it need a rewrite or not?
                    if (node.NodeType == NodeType.Unknown)
                    {
                        continue;
                    }
                }

                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetTrieNodes(null, path, node);

                node.IsPersisted = true;
            }
            _flatdiffimes.WithLabels("persistence", "trienodes").Observe(Stopwatch.GetTimestamp() - sw);

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StorageTrieNodeKeys);
            _trieNodesSortBuffer.Sort();
            _flatdiffimes.WithLabels("persistence", "trienode_sort").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            // foreach (var tn in snapshot.TrieNodes)
            foreach (var k in _trieNodesSortBuffer)
            {
                (Hash256AsKey address, TreePath path) = k;

                snapshot.TryGetStorageNode(address, path, out TrieNode node);

                if (node.FullRlp.Length == 0)
                {
                    // TODO: Need to double check this case. Does it need a rewrite or not?
                    if (node.NodeType == NodeType.Unknown)
                    {
                        continue;
                    }
                }

                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetTrieNodes(address, path, node);

                node.IsPersisted = true;
            }
            _flatdiffimes.WithLabels("persistence", "trienodes").Observe(Stopwatch.GetTimestamp() - sw);

            sw = Stopwatch.GetTimestamp();
        }
        _flatdiffimes.WithLabels("persistence", "dispose").Observe(Stopwatch.GetTimestamp() - sw);
    }

    public async ValueTask DisposeAsync()
    {
        await _clearReaderTask;
        _cachedReader?.Dispose();
    }
}
