// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Db;
using Nethermind.Network.Config;
using Nethermind.TxPool;

namespace Nethermind.Hive;

public class HivePlugin(IHiveConfig hiveConfig) : INethermindPlugin
{
    public string Name => "Hive";

    public string Description => "Plugin used for executing Hive Ethereum Tests";

    public string Author => "Nethermind";

    public bool Enabled => hiveConfig.Enabled;

    public IModule Module => new HiveModule();
}

public class HiveModule : Module
{
    private const string ExpectDeepReorgsEnvironmentVariable = "HIVE_EXPECT_DEEP_REORGS";
    private const ulong DeepReorgDepth = 512;

    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<HiveRunner>()
        .AddStep(typeof(HiveStep))
        .AddDecorator<INetworkConfig>((_, networkConfig) =>
        {
            networkConfig.FilterPeersByRecentIp = false;
            return networkConfig;
        })
        .AddDecorator<ITxPoolConfig>((_, txPoolConfig) =>
        {
            txPoolConfig.ProofsTranslationEnabled = true;
            return txPoolConfig;
        })
        .AddDecorator<IFlatDbConfig>((_, flatDbConfig) =>
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ExpectDeepReorgsEnvironmentVariable)))
            {
                // The persistence gate has one compaction chunk of look-ahead.
                flatDbConfig.MinReorgDepth = Math.Max(flatDbConfig.MinReorgDepth, DeepReorgDepth + flatDbConfig.CompactSize);
            }

            return flatDbConfig;
        })
        .ClearOrderedComponents<ITxGossipPolicy>();
}
