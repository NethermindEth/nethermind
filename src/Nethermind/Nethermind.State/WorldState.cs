// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Ethereum.Test.Base")]
[assembly: InternalsVisibleTo("Ethereum.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]
[assembly: InternalsVisibleTo("Nethermind.Benchmark")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.State;

public partial class WorldState: IWorldState
{
    private readonly ITrieStore _trieStore;
    private readonly StateTree _stateTree;

    private readonly ILogManager? _logManager;
    private readonly ILogger _logger;

    public WorldState(ITrieStore? trieStore, IKeyValueStore? codeDb, ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        _logManager = logManager;
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        _stateTree = new StateTree(trieStore, logManager);
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
    {
        CommitStorage(NullStateTracer.Instance);
        CommitState(releaseSpec, isGenesis);
    }


    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? stateTracer, bool isGenesis = false)
    {
        IWorldStateTracer? tracer = stateTracer ?? NullStateTracer.Instance;
        CommitStorage(tracer);
        CommitState(releaseSpec, tracer, isGenesis);
    }

    /// <summary>
    /// Commit persistent state and storage trees
    /// </summary>
    /// <param name="blockNumber">Current block number</param>
    public void CommitTree(long blockNumber)
    {
        foreach (KeyValuePair<Address, StorageTree> storage in _storages)
        {
            storage.Value.Commit(blockNumber);
        }
        // only needed here as there is no control over cached storage size otherwise
        _storages.Reset();

        if (_needsStateRootUpdate) RecalculateStateRoot();
        _stateTree.Commit(blockNumber);
    }

    public void Reset()
    {
        if (_logger.IsTrace) _logger.Trace("clearing storage structures");

        _storageIntraBlockCache.Clear();
        _transactionChangesSnapshots.Clear();
        _storageCurrentPosition = -1;
        Array.Clear(_storageChanges, 0, _storageChanges.Length);
        _storages.Reset();
        _originalValues.Clear();
        _storageCommittedThisRound.Clear();

        if (_logger.IsTrace) _logger.Trace("clearing state structures");
        _intraBlockCacheForState.Reset();
        _stateCommittedThisRound.Reset();
        _stateReadsForTracing.Clear();
        if (_codeDb is ReadOnlyDb db) db.ClearTempChanges();
        _stateCurrentPosition = Resettable.EmptyPosition;
        Array.Clear(_stateChanges, 0, _stateChanges.Length);
        _needsStateRootUpdate = false;
    }

    public void Restore(Snapshot snapshot)
    {
        RestoreState(snapshot.StateSnapshot);
        RestoreStorage(snapshot.StorageSnapshot);
    }

    public Snapshot TakeSnapshot(bool newTransactionStart)
    {

        if (_logger.IsTrace) _logger.Trace($"WorldStateSnapshot {_stateCurrentPosition} {_storageCurrentPosition}");
        if (newTransactionStart && _storageCurrentPosition != Resettable.EmptyPosition)
        {
            _transactionChangesSnapshots.Push(_storageCurrentPosition);
        }
        return new Snapshot(_stateCurrentPosition, _storageCurrentPosition);
    }

    private enum ChangeType
    {
        JustCache,
        Touch,
        Update,
        New,
        Delete,
        Destroy
    }
}
