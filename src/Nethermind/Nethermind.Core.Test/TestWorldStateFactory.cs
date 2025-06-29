// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

public static class TestWorldStateFactory
{
    public static WorldStateManager CreateForTest()
    {
        return CreateForTest(TestMemDbProvider.Init(), LimboLogs.Instance);
    }

    public static WorldStateManager CreateForTest(IDbProvider dbProvider, ILogManager logManager)
    {
        IPruningTrieStore trieStore = new TrieStore(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            new PruningConfig(),
            LimboLogs.Instance);
        var worldState = new WorldState(trieStore, dbProvider.CodeDb, logManager);

        return new WorldStateManager(worldState, trieStore, dbProvider, logManager);
    }
}
