// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

public static class TestWorldStateFactory
{
    public static IWorldState CreateForTest()
    {
        return CreateForTest(TestMemDbProvider.Init(), LimboLogs.Instance);
    }

    public static IWorldState CreateForTest(IDbProvider dbProvider, ILogManager logManager)
    {
        PruningConfig pruningConfig = new PruningConfig();
        FakeFinalizedStateProvider finalizedStateProvider = new FakeFinalizedStateProvider(pruningConfig.PruningBoundary);
        TrieStore trieStore = new TrieStore(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalizedStateProvider,
            pruningConfig,
            LimboLogs.Instance);
        finalizedStateProvider.TrieStore = trieStore;
        return new WorldState(trieStore, dbProvider.CodeDb, logManager);
    }

    public static (IWorldState, IStateReader) CreateForTestWithStateReader(IDbProvider? dbProvider = null, ILogManager? logManager = null)
    {
        if (dbProvider is null) dbProvider = TestMemDbProvider.Init();
        if (logManager is null) logManager = LimboLogs.Instance;

        PruningConfig pruningConfig = new PruningConfig();
        FakeFinalizedStateProvider finalizedStateProvider = new FakeFinalizedStateProvider(pruningConfig.PruningBoundary);
        TrieStore trieStore = new TrieStore(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalizedStateProvider,
            pruningConfig,
            LimboLogs.Instance);
        finalizedStateProvider.TrieStore = trieStore;
        return (new WorldState(trieStore, dbProvider.CodeDb, logManager), new StateReader(trieStore, dbProvider.CodeDb, logManager));
    }

    public static WorldStateManager CreateWorldStateManagerForTest(IDbProvider dbProvider, ILogManager logManager)
    {
        PruningConfig pruningConfig = new PruningConfig();
        FakeFinalizedStateProvider finalizedStateProvider = new FakeFinalizedStateProvider(pruningConfig.PruningBoundary);
        TrieStore trieStore = new TrieStore(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalizedStateProvider,
            pruningConfig,
            LimboLogs.Instance);
        finalizedStateProvider.TrieStore = trieStore;
        var worldState = new WorldState(trieStore, dbProvider.CodeDb, logManager);

        return new WorldStateManager(worldState, trieStore, dbProvider, logManager);
    }
}
