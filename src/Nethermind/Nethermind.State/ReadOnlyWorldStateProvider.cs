// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class ReadOnlyWorldStateProvider : IWorldStateProvider
{
    private readonly IReadOnlyTrieStore _readOnlyTrieStore;
    private readonly IStateReader _stateReader;
    public ITrieStore TrieStore => _readOnlyTrieStore;

    public ReadOnlyWorldStateProvider(
        IDbProvider dbProvider,
        IReadOnlyTrieStore readOnlyTrieStore,
        ILogManager logManager)
    {
        IReadOnlyDbProvider readOnlyDbProvider = dbProvider.AsReadOnly(false);
        IDb codeDb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);

        _stateReader = new StateReader(readOnlyTrieStore, codeDb, logManager);
        _readOnlyTrieStore = readOnlyTrieStore;
    }

    public virtual IWorldState GetWorldState(BlockHeader header) => throw new InvalidOperationException("world state is not supported");

    public virtual IStateReader GetStateReader(Hash256 stateRoot)
    {
        // TODO: return corresponding worldState depending on HasStateForRoot(stateRoot)
        return _stateReader;
    }

}
