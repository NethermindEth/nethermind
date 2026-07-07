// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class TestMergeModule(ITxPoolConfig txPoolConfig, IModule? mergeModule) : Module
{
    public TestMergeModule(ITxPoolConfig txPoolConfig) : this(txPoolConfig, new MergePluginModule())
    {
    }

    public TestMergeModule(IConfigProvider configProvider) : this(configProvider.GetConfig<ITxPoolConfig>(), new MergePluginModule())
    {
    }

    public TestMergeModule(IConfigProvider configProvider, IModule? mergeModule) : this(configProvider.GetConfig<ITxPoolConfig>(), mergeModule)
    {
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        // Optional: AuRa passes null and installs AuRaMergeModule itself (see MergeTestBlockchain.MergeModule).
        if (mergeModule is not null)
            builder.AddModule(mergeModule);

        builder
            .AddDecorator<IRewardCalculatorSource, MergeRewardCalculatorSource>()

            // Validators
            .AddDecorator<IGossipPolicy, MergeGossipPolicy>()

            // Block production related.
            .AddScoped<PostMergeBlockProducerFactory>()

            // Engine rpc
            .AddSingleton<IEngineRequestsTracker, NoEngineRequestsTracker>()
            ;

        if (txPoolConfig.BlobsSupport.SupportsReorgs())
        {
            builder.AddSingleton<ProcessedTransactionsDbCleaner, IBlockTree, IDbProvider, ILogManager>(
                static (blockTree, dbProvider, logManager) => new ProcessedTransactionsDbCleaner(
                    blockTree,
                    dbProvider.BlobTransactionsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs),
                    logManager));
        }
    }
}
