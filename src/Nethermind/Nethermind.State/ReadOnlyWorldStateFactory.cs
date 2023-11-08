// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class ReadOnlyWorldStateFactory: IWorldStateFactory
{
    private IReadOnlyTrieStore? _readOnlyTrieStore;
    private ILogManager _logManager;
    private readonly IDbProvider _dbProvider;

    public ReadOnlyWorldStateFactory(
        IDbProvider dbProvider,
        IReadOnlyTrieStore? readOnlyTrieStore,
        ILogManager logManager
    )
    {
        _readOnlyTrieStore = readOnlyTrieStore;
        _dbProvider = dbProvider;
        _logManager = logManager;
    }

    public IWorldState CreateWorldState()
    {
        IKeyValueStore codeDb = _dbProvider.AsReadOnly(false).GetDb<IDb>(DbNames.Code);
        return new WorldState(_readOnlyTrieStore, codeDb, _logManager);
    }

    public IStateReader CreateStateReader()
    {
        IKeyValueStore codeDb = _dbProvider.AsReadOnly(false).GetDb<IDb>(DbNames.Code);
        return new StateReader(_readOnlyTrieStore, codeDb, _logManager);
    }

    public (IWorldState, IStateReader, Action) CreateResettableWorldState()
    {
        ReadOnlyDbProvider readOnlyDbProvider = _dbProvider.AsReadOnly(false);
        IKeyValueStore codeDb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        return (
            new WorldState(_readOnlyTrieStore, codeDb, _logManager),
            new StateReader(_readOnlyTrieStore, codeDb, _logManager),
            readOnlyDbProvider.ClearTempChanges
        );
    }
}
