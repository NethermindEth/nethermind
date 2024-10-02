// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverridableWorldStateManager : IWorldStateManager
{
    private readonly IDb _codeDb;
    private readonly StateReader _reader;
    private readonly WorldState _state;

    private readonly OverlayTrieStore _overlayTrieStore;
    private readonly ILogManager? _logManager;

    public OverridableWorldStateManager(IDbProvider dbProvider, IReadOnlyTrieStore trieStore, ILogManager? logManager)
    {
        dbProvider = new ReadOnlyDbProvider(dbProvider, true);
        OverlayTrieStore overlayTrieStore = new(dbProvider.StateDb, trieStore, logManager);

        _logManager = logManager;
        _codeDb = dbProvider.GetDb<IDb>(DbNames.Code);
        _reader = new(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);
        _state = new(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);
        _overlayTrieStore = overlayTrieStore;
    }

    public IWorldState GlobalWorldState => _state;
    public IStateReader GlobalStateReader => _reader;
    public IReadOnlyTrieStore TrieStore => _overlayTrieStore.AsReadOnly();

    public IWorldState CreateResettableWorldState(IWorldState? forWarmup = null)
    {
        // TODO add if needed?
        if (forWarmup is not null)
            throw new NotSupportedException("Overridable world state with warm up is not supported.");

        return new OverridableWorldState(_overlayTrieStore, _codeDb, _logManager);
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _overlayTrieStore.ReorgBoundaryReached += value;
        remove => _overlayTrieStore.ReorgBoundaryReached -= value;
    }
}
