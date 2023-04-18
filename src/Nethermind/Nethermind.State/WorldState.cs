// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
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
        CommitStorage((IStorageTracer)NullStateTracer.Instance);
        CommitState(releaseSpec, isGenesis);
    }


    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? tracer, bool isGenesis = false)
    {
        CommitState(releaseSpec, tracer, isGenesis);
        CommitStorage(tracer ?? (IStorageTracer)NullStateTracer.Instance);
    }


    /// <summary>
    /// Commit persisent storage trees
    /// </summary>
    /// <param name="blockNumber">Current block number</param>
    public void CommitTree(long blockNumber)
    {
        // _logger.Warn($"Storage block commit {blockNumber}");
        foreach (KeyValuePair<Address, StorageTree> storage in _storages)
        {
            storage.Value.Commit(blockNumber);
        }

        // TODO: maybe I could update storage roots only now?

        // only needed here as there is no control over cached storage size otherwise
        _storages.Reset();

        CommitStateTree(blockNumber);
    }

    public void Reset()
    {
        if (_logger.IsTrace) _logger.Trace("Resetting storage");

        _storageIntraBlockCache.Clear();
        _transactionChangesSnapshots.Clear();
        _currentStoragePosition = -1;
        Array.Clear(_storageChanges, 0, _storageChanges.Length);

        _storages.Reset();
        _originalValues.Clear();
        _storageCommittedThisRound.Clear();

        ResetState();
    }

    public void Restore(Snapshot snapshot)
    {
        RestoreStateSnapshot(snapshot.StateSnapshot);
        RestoreStorage(snapshot.StorageSnapshot);
    }

    public Snapshot TakeSnapshot(bool newTransactionStart)
    {
        int storageSnapshot = TakeStorageSnapshot(newTransactionStart);
        return new Snapshot(_stateCurrentPosition, storageSnapshot);
    }

}
