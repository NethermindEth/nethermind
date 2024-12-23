// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverridableWorldStateManager : IOverridableWorldScope
{
    private readonly ReadOnlyDbProvider _readOnlyDbProvider;
    private readonly StateReader _reader;

    private readonly OverlayTrieStore _overlayTrieStore;
    private readonly ILogManager? _logManager;

    public OverridableWorldStateManager(IDbProvider dbProvider, IReadOnlyTrieStore trieStore, ILogManager? logManager)
    {
        dbProvider = _readOnlyDbProvider = new(dbProvider, true);
        OverlayTrieStore overlayTrieStore = new(dbProvider.StateDb, trieStore, logManager);

        _logManager = logManager;
        _reader = new(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);
        _overlayTrieStore = overlayTrieStore;

        WorldState = new OverridableWorldState(_overlayTrieStore, _readOnlyDbProvider, _logManager);
    }

    public IOverridableWorldState WorldState { get; }
    public IStateReader GlobalStateReader => _reader;
    public IReadOnlyTrieStore TrieStore => _overlayTrieStore.AsReadOnly();
}
