// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

public static class TestWorldStateFactory
{
    public static WorldStateManager CreateForTest(IDbProvider dbProvider, ILogManager logManager)
    {
        IPruningTrieStore trieStore = TestTrieStoreFactory.Build(dbProvider.StateDb, logManager);
        IWorldState worldState = new WorldState(trieStore, dbProvider.CodeDb, logManager);

        return new WorldStateManager(worldState, trieStore, dbProvider, logManager);
    }
}
