// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Core.Test;

public static class TestWorldStateFactory
{
    public static IWorldState CreateForTest(IDbProvider? dbProvider = null, ILogManager? logManager = null)
    {
        dbProvider ??= TestMemDbProvider.Init();
        logManager ??= LimboLogs.Instance;
        TestRawTrieStore trieStore = TestTrieStoreFactory.Build(dbProvider.GetDb<IDb>(DbNames.State), logManager);
        return new WorldState(new TrieStoreScopeProvider(trieStore, dbProvider.CodeDb, logManager), logManager);
    }

    public static (IWorldState, IStateReader) CreateForTestWithStateReader(IDbProvider? dbProvider = null, ILogManager? logManager = null)
    {
        dbProvider ??= TestMemDbProvider.Init();
        logManager ??= LimboLogs.Instance;

        TestRawTrieStore trieStore = TestTrieStoreFactory.Build(dbProvider.GetDb<IDb>(DbNames.State), logManager);
        return (new WorldState(new TrieStoreScopeProvider(trieStore, dbProvider.CodeDb, logManager), logManager), new StateReader(trieStore, dbProvider.CodeDb, logManager));
    }

    public static (IWorldStateScopeProvider scopeProvider, IContainer container) CreateFlatScopeProvider()
    {
        IContainer container = BuildFlatContainer();
        IWorldStateManager wsm = container.Resolve<IWorldStateManager>();
        return (wsm.GlobalWorldState, container);
    }

    public static (IWorldState worldState, IStateReader reader, IContainer container) CreateFlatForTestWithStateReader(ILogManager? logManager = null)
    {
        logManager ??= LimboLogs.Instance;
        IContainer container = BuildFlatContainer();
        IWorldStateManager wsm = container.Resolve<IWorldStateManager>();
        return (new WorldState(wsm.GlobalWorldState, logManager), wsm.GlobalStateReader, container);
    }

    private static IContainer BuildFlatContainer()
    {
        ConfigProvider configProvider = new();
        configProvider.GetConfig<IFlatDbConfig>().Enabled = true;
        return new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .Build();
    }

}
