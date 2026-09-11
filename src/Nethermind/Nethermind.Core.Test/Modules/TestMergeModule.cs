// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Consensus.Rewards;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Core.Test.Modules;

public class TestMergeModule(IModule? mergeModule) : Module
{
    public TestMergeModule() : this(new MergePluginModule())
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

            // Block production related.
            .AddScoped<PostMergeBlockProducerFactory>()

            // Engine rpc
            .AddSingleton<IEngineRequestsTracker, NoEngineRequestsTracker>()
            ;
    }
}
