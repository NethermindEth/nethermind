// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Core.Test;

public static class TestWorldStateFactory
{
    public static IWorldState CreateForTest(IDbProvider? dbProvider = null, ILogManager? logManager = null) =>
        CreateForTest(UnavailableStateHeaderProvider.Instance, dbProvider, logManager);

    public static IWorldState CreateForTest(IStateHeaderProvider stateHeaderProvider, IDbProvider? dbProvider = null, ILogManager? logManager = null)
    {
        PruningConfig pruningConfig = new();
        TestFinalizedStateProvider finalizedStateProvider = new(pruningConfig.PruningBoundary);
        dbProvider ??= TestMemDbProvider.Init();
        logManager ??= LimboLogs.Instance;
        TrieStore trieStore = new(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalizedStateProvider,
            pruningConfig,
            LimboLogs.Instance);
        finalizedStateProvider.TrieStore = trieStore;
        return new WorldState(new TrieStoreScopeProvider(trieStore, dbProvider.CodeDb, stateHeaderProvider, logManager), logManager);
    }

    public static (IWorldState, IStateReader) CreateForTestWithStateReader(IDbProvider? dbProvider = null, ILogManager? logManager = null, IStateHeaderProvider? stateHeaderProvider = null)
    {
        dbProvider ??= TestMemDbProvider.Init();
        logManager ??= LimboLogs.Instance;
        stateHeaderProvider ??= UnavailableStateHeaderProvider.Instance;

        PruningConfig pruningConfig = new();
        TestFinalizedStateProvider finalizedStateProvider = new(pruningConfig.PruningBoundary);
        TrieStore trieStore = new(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalizedStateProvider,
            pruningConfig,
            LimboLogs.Instance);
        finalizedStateProvider.TrieStore = trieStore;
        return (new WorldState(new TrieStoreScopeProvider(trieStore, dbProvider.CodeDb, stateHeaderProvider, logManager), logManager), new StateReader(trieStore, dbProvider.CodeDb, logManager));
    }

    public static (IWorldStateScopeProvider scopeProvider, IContainer container) CreateFlatScopeProvider(IStateHeaderProvider stateHeaderProvider)
    {
        IContainer container = BuildFlatContainer(stateHeaderProvider);
        IWorldStateManager wsm = container.Resolve<IWorldStateManager>();
        return (wsm.GlobalWorldState, container);
    }

    public static (IWorldState worldState, IStateReader reader, IContainer container) CreateFlatForTestWithStateReader(ILogManager? logManager = null)
    {
        logManager ??= LimboLogs.Instance;
        IContainer container = BuildFlatContainer(UnavailableStateHeaderProvider.Instance);
        IWorldStateManager wsm = container.Resolve<IWorldStateManager>();
        return (new WorldState(wsm.GlobalWorldState, logManager), wsm.GlobalStateReader, container);
    }

    private static IContainer BuildFlatContainer(IStateHeaderProvider stateHeaderProvider)
    {
        ConfigProvider configProvider = new();
        configProvider.GetConfig<IFlatDbConfig>().Enabled = true;
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider));
        if (stateHeaderProvider is not null) builder.AddSingleton(stateHeaderProvider);
        return builder.Build();
    }

    public static WorldStateManager CreateWorldStateManagerForTest(IDbProvider dbProvider, ILogManager logManager)
    {
        PruningConfig pruningConfig = new();
        TestFinalizedStateProvider finalizedStateProvider = new(pruningConfig.PruningBoundary);
        TrieStore trieStore = new(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalizedStateProvider,
            pruningConfig,
            LimboLogs.Instance);
        finalizedStateProvider.TrieStore = trieStore;
        TrieStoreScopeProvider worldState = new(trieStore, dbProvider.CodeDb, UnavailableStateHeaderProvider.Instance, logManager);

        return new WorldStateManager(worldState, trieStore, dbProvider,
            new StateBoundaryStore(dbProvider.StateDb, dbProvider.BlockInfosDb, retentionWindowBlocks: null, logManager),
            UnavailableStateHeaderProvider.Instance, logManager);
    }
}
