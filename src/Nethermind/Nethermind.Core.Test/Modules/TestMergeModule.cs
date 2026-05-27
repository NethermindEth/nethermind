// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class TestMergeModule : Module
{
    public TestMergeModule() { }
    public TestMergeModule(ITxPoolConfig _) { }
    public TestMergeModule(IConfigProvider _) { }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new MergePluginModule())

            .AddDecorator<IRewardCalculatorSource, MergeRewardCalculatorSource>()

            // Validators
            .AddDecorator<IGossipPolicy, MergeGossipPolicy>()
            .AddSingleton<IBlockPreprocessorStep, MergeProcessingRecoveryStep>()

            .AddDecorator<IBlockProductionPolicy, MergeBlockProductionPolicy>()

            // Block production related.
            .AddDecorator<IBlockProductionPolicy, MergeBlockProductionPolicy>()
            .AddScoped<PostMergeBlockProducerFactory>()
            .AddDecorator<IBlockProducerFactory, MergeBlockProducerFactory>()

            // Engine rpc
            .AddSingleton<IEngineRequestsTracker, NoEngineRequestsTracker>()
            ;

    }
}
