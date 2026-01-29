// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.ParallelProcessing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.State;
using Nethermind.Trie;


namespace Nethermind.Init.Modules;

public class ParallelModule(IBlocksConfig blocksConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (blocksConfig.ParallelBlockProcessing)
        {
            builder
                .AddSingleton<NodeStorageCache>()
                .AddSingleton<IMainProcessingModule, ParallelMainProcessingModule>();
        }
    }

    public class ParallelMainProcessingModule : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder
                .AddSingleton<PreBlockCaches>()
                .AddScoped<IBlockCachePreWarmer, NoBlockCachePreWarmer>()
                .Add<PrewarmerEnvFactory>()
                .Add<ParallelEnvFactory>()
                .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, ParallelBlockValidationTransactionsExecutor>();
        }
    }
}
