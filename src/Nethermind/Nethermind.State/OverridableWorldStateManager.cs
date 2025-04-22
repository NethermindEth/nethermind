// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.State;

public class OverridableWorldStateManager : IOverridableWorldScope
{
    private readonly IStateReader _reader;

    public OverridableWorldStateManager(IDbProvider dbProvider, IReadOnlyTrieStore trieStore, ILogManager? logManager)
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        OverlayTrieStore overlayTrieStore = new(readOnlyDbProvider.StateDb, trieStore, logManager);

        _reader = new StateReader(overlayTrieStore, readOnlyDbProvider.CodeDb, logManager);

        WorldState = new OverridableWorldState(overlayTrieStore, readOnlyDbProvider, logManager);
    }

    public OverridableWorldStateManager(IDbProvider dbProvider, IReadOnlyVerkleTreeStore trieStore, ILogManager? logManager)
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        OverlayVerkleTreeStore overlayTrieStore = new(readOnlyDbProvider, trieStore, logManager);

        _reader = new VerkleStateReader(overlayTrieStore, readOnlyDbProvider.CodeDb, logManager);

        WorldState = new OverridableVerkleWorldState(overlayTrieStore, readOnlyDbProvider, logManager);
    }

    public IOverridableWorldState WorldState { get; }
    public IStateReader GlobalStateReader => _reader;
}
