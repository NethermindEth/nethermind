// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Mainly to make it easier for test
/// </summary>
public class ReadOnlyWorldStateManager: IWorldStateManager
{
    private IReadOnlyTrieStore? _readOnlyTrieStore;
    private ILogManager _logManager;
    private readonly IDbProvider _dbProvider;

    public ReadOnlyWorldStateManager(
        IDbProvider dbProvider,
        IReadOnlyTrieStore? readOnlyTrieStore,
        ILogManager logManager
    )
    {
        _readOnlyTrieStore = readOnlyTrieStore;
        _dbProvider = dbProvider;
        _logManager = logManager;

        IKeyValueStore codeDb = _dbProvider.AsReadOnly(false).GetDb<IDb>(DbNames.Code);
        GlobalStateReader = new StateReader(_readOnlyTrieStore, codeDb, _logManager);
    }

    public virtual IWorldState GlobalWorldState => throw new InvalidOperationException("global world state not supported");

    public IStateReader GlobalStateReader { get; }

    public (IWorldState, IStateReader, Action) CreateResettableWorldState()
    {
        ReadOnlyDbProvider readOnlyDbProvider = _dbProvider.AsReadOnly(false);
        ReadOnlyDb codeDb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        return (
            new WorldState(_readOnlyTrieStore, codeDb, _logManager),
            new StateReader(_readOnlyTrieStore, codeDb, _logManager),
            () =>
            {
                readOnlyDbProvider.ClearTempChanges();
                codeDb.ClearTempChanges();
            });
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }
}
