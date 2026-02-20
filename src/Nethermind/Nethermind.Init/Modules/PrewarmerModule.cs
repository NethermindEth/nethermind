// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Init.Modules;

public class PrewarmerModule(IBlocksConfig blocksConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (blocksConfig.PreWarmStateOnBlockProcessing)
        {
            builder

                // Note: There is a special logic for this in `PruningTrieStateFactory`.
                .AddSingleton<NodeStorageCache>()

                // Note: Need a small modification to have this work on all branch processor due to the shared
                // NodeStorageCache and the FrozenDictionary and the fact that some processing does not have
                // branch processor, and use block processor instead.
                .AddSingleton<IMainProcessingModule, PrewarmerMainProcessingModule>();
        }
    }

    public class PrewarmerMainProcessingModule : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder
                // Singleton so that all child env share the same caches. Note: this module is applied per-processing
                // module, so singleton here is like scoped but exclude inner prewarmer lifetime.
                .AddSingleton<PreBlockCaches>()
                .AddScoped<IBlockCachePreWarmer, BlockCachePreWarmer>()
                .Add<PrewarmerEnvFactory>()

                // These are the actual decorated component that provide cached result
                .AddDecorator<IWorldStateScopeProvider>((ctx, worldStateScopeProvider) =>
                {
                    if (worldStateScopeProvider is PrewarmerScopeProvider) return worldStateScopeProvider; // Inner world state
                    return new PrewarmerScopeProvider(
                        worldStateScopeProvider,
                        ctx.Resolve<PreBlockCaches>(),
                        populatePreBlockCache: false
                    );
                })
                .AddDecorator<ICodeInfoRepository>((ctx, originalCodeInfoRepository) =>
                {
                    IBlocksConfig blocksConfig = ctx.Resolve<IBlocksConfig>();
                    PreBlockCaches preBlockCaches = ctx.Resolve<PreBlockCaches>();
                    IPrecompileProvider precompileProvider = ctx.Resolve<IPrecompileProvider>();
                    // Note: The use of FrozenDictionary means that this cannot be used for other processing env also due to risk of memory leak.
                    return new CachedCodeInfoRepository(precompileProvider, originalCodeInfoRepository,
                        blocksConfig.CachePrecompilesOnBlockProcessing ? preBlockCaches?.PrecompileCache : null);
                });
        }
    }
}
