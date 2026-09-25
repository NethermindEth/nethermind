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
    public static IWorldState CreateForTest(IDbProvider? dbProvider = null, ILogManager? logManager = null) =>
        CreateForTest(UnavailableStateHeaderProvider.Instance, dbProvider, logManager);

    public static IWorldState CreateForTest(IStateHeaderProvider stateHeaderProvider, IDbProvider? dbProvider = null, ILogManager? logManager = null)
    {
        dbProvider ??= TestMemDbProvider.Init();
        logManager ??= LimboLogs.Instance;
        TestRawTrieStore trieStore = new(dbProvider.GetDb<IDb>(DbNames.State));
        return new WorldState(new TrieStoreScopeProvider(trieStore, dbProvider.CodeDb, stateHeaderProvider, logManager), logManager);
    }

    public static (IWorldStateScopeProvider scopeProvider, IContainer container) CreateFlatScopeProvider(IStateHeaderProvider stateHeaderProvider)
    {
        IContainer container = BuildFlatContainer(stateHeaderProvider);
        IWorldStateManager wsm = container.Resolve<IWorldStateManager>();
        return (wsm.GlobalWorldState, container);
    }

    public static (IWorldState worldState, IStateReader reader, IContainer container) CreateFlatForTestWithStateReader(ILogManager? logManager = null, IStateHeaderProvider? stateHeaderProvider = null)
    {
        logManager ??= LimboLogs.Instance;
        IContainer container = BuildFlatContainer(stateHeaderProvider ?? UnavailableStateHeaderProvider.Instance);
        IWorldStateManager wsm = container.Resolve<IWorldStateManager>();
        return (new WorldState(wsm.GlobalWorldState, logManager), wsm.GlobalStateReader, container);
    }

    private static IContainer BuildFlatContainer(IStateHeaderProvider stateHeaderProvider)
    {
        ConfigProvider configProvider = new();
        return new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddSingleton(stateHeaderProvider)
            .Build();
    }

}
