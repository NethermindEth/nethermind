// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using NSubstitute;

namespace Nethermind.Synchronization.Test;

public class TestSynchronizerModule(
    ISyncConfig syncConfig,
    Func<INodeStorage, ILogManager, ISnapTrieFactory>? factoryCreator = null) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new SynchronizerModule(syncConfig))
            .AddModule(new DbModule(new InitConfig(), new ReceiptConfig(), syncConfig))
            .AddSingleton<IDbFactory>((_) => new MemDbFactory())
            .Map<INodeStorage, IDbProvider>(dbProvider => new NodeStorage(dbProvider.StateDb))
            .AddSingleton<IBlockTree>(Substitute.For<IBlockTree>())
            .AddSingleton<ITimerFactory>(Substitute.For<ITimerFactory>())
            .AddSingleton<ISyncConfig>(syncConfig)
            .AddSingleton<IBetterPeerStrategy>(new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance))
            .AddSingleton<INodeStatsManager, NodeStatsManager>()
            .AddSingleton<CancelOnDisposeToken>()
            .AddSingleton<ILogManager>(LimboLogs.Instance);

        // Override factory if provided (must come after SynchronizerModule to override)
        if (factoryCreator is not null)
        {
            builder.Register(c => factoryCreator(
                c.Resolve<INodeStorage>(),
                c.Resolve<ILogManager>()
            )).As<ISnapTrieFactory>().SingleInstance();
        }
    }
}
