// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;

namespace Nethermind.State.Flat;

public class PersistenceRunner
{
    private readonly FlatDiffRepository _flatDiffRepository;
    private readonly ILogger _logger;
    private readonly Histogram _flatdiffimes;
    private readonly int _minimumPruningBoundary;
    private readonly int _forcedPruningBoundary;
    private readonly int _compactSize;
    private readonly IFinalizedStateProvider _finalizedStateProvider;
    private List<(Hash256AsKey, TreePath)> _trieNodesSortBuffer = new List<(Hash256AsKey, TreePath)>(); // Presort make it faster
    private readonly IPersistence _persistence;

    public PersistenceRunner(FlatDiffRepository flatDiffRepository, FlatDiffRepository.Configuration configuration, IFinalizedStateProvider finalizedStateProvider, IPersistence persistence, ILogManager logManager)
    {
        _flatDiffRepository = flatDiffRepository;
        _finalizedStateProvider = finalizedStateProvider;
        _persistence = persistence;
        _logger = logManager.GetClassLogger();
        _flatdiffimes = FlatDiffRepository._flatdiffimes;
        _forcedPruningBoundary = configuration.ForcedPruningBoundary;
        _minimumPruningBoundary = configuration.Boundary;
        _compactSize = configuration.CompactSize;
    }

    internal void AddToPersistence()
    {
        // Attempt to add snapshots into bigcache
        while (true)
        {
            Snapshot? snapshotToSave = null;
            long sw = Stopwatch.GetTimestamp();
            using (_flatDiffRepository.EnterRepolock())
            {
                _flatdiffimes.WithLabels("add_to_persistence", "repolock").Observe(Stopwatch.GetTimestamp() - sw);
                sw = Stopwatch.GetTimestamp();
                long lastSnapshotNumber = _flatDiffRepository.GetLastSnapshotId()?.blockNumber ?? 0;
                StateId currentPersistedState = _flatDiffRepository.GetCurrentPersistedStateId();
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
                    using ArrayPoolList<StateId> nextBlockStates = _flatDiffRepository.GetStatesAtBlockNumber(currentPersistedState.blockNumber + 1);

                    // Just pick the first one
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
            Add(snapshotToSave);
            snapshotToSave.Dispose();

            sw = Stopwatch.GetTimestamp();

            // And we remove it
            _flatDiffRepository.OnStatePersisted(snapshotToSave.To);
            _flatdiffimes.WithLabels("add_to_persistence", "cleanup").Observe(Stopwatch.GetTimestamp() - sw);

            sw = Stopwatch.GetTimestamp();

            _flatdiffimes.WithLabels("add_to_persistence", "reorg_boundary").Observe(Stopwatch.GetTimestamp() - sw);
        }
    }

    public void Add(Snapshot snapshot)
    {
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

        _flatDiffRepository.ClearReaderCache();
    }


}
