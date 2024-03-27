// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverlayWorldStateManager(
        IReadOnlyDbProvider dbProvider,
    OverlayTrieStore overlayTrieStore,
    ILogManager? logManager)
    : IWorldStateManager
{
    private readonly IDb _codeDb = dbProvider.GetDb<IDb>(DbNames.Code);

    private readonly WorldState _state = new WorldState(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);

    private readonly StateReader _reader = new StateReader(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);

    public IWorldState GlobalWorldState => _state;

    public IStateReader GlobalStateReader => _reader;

    public IReadOnlyTrieStore TrieStore { get; } = overlayTrieStore.AsReadOnly();

    public IWorldState CreateResettableWorldState() => new WorldState(overlayTrieStore, _codeDb, logManager);

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => overlayTrieStore.ReorgBoundaryReached += value;
        remove => overlayTrieStore.ReorgBoundaryReached -= value;
    }
}
