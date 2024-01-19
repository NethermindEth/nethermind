// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverlayWorldStateManager(
    IWorldStateManager worldStateManager,
    IReadOnlyDbProvider dbProvider,
    OverlayTrieStore overlayTrieStore,
    ILogManager? logManager)
    : IWorldStateManager
{
    private readonly IDb _codeDb = dbProvider.GetDb<IDb>(DbNames.Code);

    public IWorldState GlobalWorldState => worldStateManager.GlobalWorldState;

    public IStateReader GlobalStateReader => worldStateManager.GlobalStateReader;

    public IReadOnlyTrieStore TrieStore { get; } = overlayTrieStore.AsReadOnly();

    public IWorldState CreateResettableWorldState() => new WorldState(overlayTrieStore, _codeDb, logManager);

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => overlayTrieStore.ReorgBoundaryReached += value;
        remove => overlayTrieStore.ReorgBoundaryReached -= value;
    }
}
