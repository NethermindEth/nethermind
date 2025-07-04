// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverridableWorldStateManager : IOverridableWorldScope
{
    private readonly StateReader _reader;
    private readonly IReadOnlyDbProvider _dbProvider;

    public OverridableWorldStateManager(IDbProvider dbProvider, IReadOnlyTrieStore trieStore, ILogManager? logManager)
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        _dbProvider = readOnlyDbProvider;
        OverlayTrieStore overlayTrieStore = new(readOnlyDbProvider.StateDb, trieStore);
        _reader = new(overlayTrieStore, readOnlyDbProvider.CodeDb, logManager);
        WorldState = new WorldState(overlayTrieStore, readOnlyDbProvider.CodeDb, logManager, null, true);
    }

    public IVisitingWorldState WorldState { get; }
    public IDisposable BeginScope(BlockHeader? header)
    {
        WorldState.SetBaseBlock(header);
        return new Reactive.AnonymousDisposable(() => ResetOverrides());
    }

    public IStateReader GlobalStateReader => _reader;
    public void ResetOverrides()
    {
        WorldState.SetBaseBlock(null);
        _dbProvider.ClearTempChanges();
    }
}
