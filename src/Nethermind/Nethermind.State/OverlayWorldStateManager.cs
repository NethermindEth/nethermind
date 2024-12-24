// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverlayWorldStateManager(
    IReadOnlyDbProvider dbProvider,
    OverlayTrieStore overlayTrieStore,
    ILogManager? logManager)
    : IWorldStateManager
{
    private readonly IDb _codeDb = dbProvider.GetDb<IDb>(DbNames.Code);

    private readonly StateReader _reader = new(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);

    private readonly WorldState _state = new(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);

    public IWorldState GlobalWorldState => _state;

    public IStateReader GlobalStateReader => _reader;

    public IReadOnlyTrieStore TrieStore { get; } = overlayTrieStore.AsReadOnly();
    public bool SupportHashLookup => overlayTrieStore.Scheme == INodeStorage.KeyScheme.Hash;

    public IWorldState CreateResettableWorldState(IWorldState? forWarmup = null)
    {
        ITrieStore trieStore = forWarmup is IPreBlockCaches { Caches: { } preBlockCaches }
            ? new PreCachedTrieStore(overlayTrieStore, preBlockCaches.RlpCache)
            : overlayTrieStore;

        return new WorldState(trieStore, _codeDb, logManager);
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => overlayTrieStore.ReorgBoundaryReached += value;
        remove => overlayTrieStore.ReorgBoundaryReached -= value;
    }
}
