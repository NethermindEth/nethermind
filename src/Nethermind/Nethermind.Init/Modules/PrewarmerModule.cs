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
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Init.Modules;

public class PrewarmerModule(IBlocksConfig blocksConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (!blocksConfig.PreWarmStateOnBlockProcessing) return;

        // NodeStorageCache (trie-node RLP cache) is independent of speculative-execution
        // warmup: PruningTrieStateFactory wraps the main world store with it, and
        // WorldStateManager also wraps _readOnlyTrieStore (used by Block-STM workers' resettable
        // world state) when it sees the cache via DI. So we always register it when prewarming
        // is enabled — Block-STM benefits from the trie-node cache even though it skips
        // PreBlockCaches and PrewarmerScopeProvider.
        builder.AddSingleton<NodeStorageCache>();

        // The full speculative-execution prewarmer (PrewarmerScopeProvider + PreBlockCaches +
        // PrecompileCachedCodeInfoRepository) duplicates work Block-STM already does via MVMM,
        // so only wire it on the sequential path.
        if (!blocksConfig.BlockStmEnabled)
        {
            builder.AddSingleton<IMainProcessingModule, PrewarmerMainProcessingModule>();
        }
    }

    public class PrewarmerMainProcessingModule : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) => builder
                // Singleton so that all child env share the same caches. Note: this module is applied per-processing
                // module, so singleton here is like scoped but exclude inner prewarmer lifetime.
                .AddSingleton<PreBlockCaches>()
                .AddScoped<IBlockCachePreWarmer, BlockCachePreWarmer>()

                // This class create the block processing env with worldstate that populate the cache
                .Add<PrewarmerEnvFactory>()

                // These are the actual decorated component that provide cached result
                .AddDecorator<IWorldStateScopeProvider>((ctx, worldStateScopeProvider) =>
                {
                    if (worldStateScopeProvider is PrewarmerScopeProvider) return worldStateScopeProvider; // Inner world state
                    return new PrewarmerScopeProvider(
                        worldStateScopeProvider,
                        ctx.Resolve<PreBlockCaches>(),
                        ctx.Resolve<ILogManager>(),
                        isPrewarmer: false
                    );
                })
                .AddDecorator<ICodeInfoRepository>((ctx, originalCodeInfoRepository) =>
                {
                    IBlocksConfig blocksConfig = ctx.Resolve<IBlocksConfig>();
                    PreBlockCaches preBlockCaches = ctx.Resolve<PreBlockCaches>();
                    IPrecompileProvider precompileProvider = ctx.Resolve<IPrecompileProvider>();
                    IWorldState worldState = ctx.Resolve<IWorldState>();
                    // Note: The use of FrozenDictionary means that this cannot be used for other processing env also due to risk of memory leak.
                    return new PrecompileCachedCodeInfoRepository(worldState, precompileProvider, originalCodeInfoRepository,
                        blocksConfig.CachePrecompilesOnBlockProcessing ? preBlockCaches?.PrecompileCache : null);
                });
    }
}
