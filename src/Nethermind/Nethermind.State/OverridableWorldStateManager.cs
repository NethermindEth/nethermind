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
    private readonly KnownHeadersScopeProvider _worldState;

    public OverridableWorldStateManager(IDbProvider dbProvider, IReadOnlyTrieStore trieStore, IStateHeaderProvider stateHeaderProvider, ILogManager logManager)
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        _dbProvider = readOnlyDbProvider;
        OverlayTrieStore overlayTrieStore = new(readOnlyDbProvider.StateDb, trieStore);
        _reader = new(overlayTrieStore, readOnlyDbProvider.CodeDb, logManager);
        _worldState = new KnownHeadersScopeProvider(stateHeaderProvider,
            headerProvider => new TrieStoreScopeProvider(overlayTrieStore, readOnlyDbProvider.CodeDb, headerProvider, logManager, codeDbIsPersistent: false));
    }

    public IWorldStateScopeProvider WorldState => _worldState;

    public IStateReader GlobalStateReader => _reader;

    public void ResetOverrides()
    {
        _dbProvider.ClearTempChanges();
        _worldState.Clear();
    }

    public void Dispose() => _dbProvider.Dispose();
}
