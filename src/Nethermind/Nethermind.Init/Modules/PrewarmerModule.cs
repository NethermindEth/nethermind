// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Init.Modules;

public class PrewarmerModule(IBlocksConfig blocksConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (blocksConfig.PreWarming != PreWarmMode.None)
        {
            builder

                // Note: There is a special logic for this in `PruningTrieStateFactory`.
                .AddSingleton<NodeStorageCache>()

                // Parent scope so test modules can override; child scope's PreBlockCaches falls through here.
                .AddSingleton<PreBlockCachesConfig>()

                // Note: Need a small modification to have this work on all branch processor due to the shared
                // NodeStorageCache and the FrozenDictionary and the fact that some processing does not have
                // branch processor, and use block processor instead.
                .AddSingleton<IMainProcessingModule, PrewarmerMainProcessingModule>();
        }
    }

    public class PrewarmerMainProcessingModule(IBlocksConfig blocksConfig) : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder
                // Singleton so that all child env share the same caches. Note: this module is applied per-processing
                // module, so singleton here is like scoped but exclude inner prewarmer lifetime.
                .AddSingleton<PreBlockCaches>()
                .AddSingleton<IPrewarmerState, PreBlockCaches>(static caches => new PrewarmerState(caches, isPrewarmer: false))
                .AddScoped<IBlockCachePreWarmer, BlockCachePreWarmer>()
                // System-contract access-list hints the prewarmer warms alongside tx addresses.
                .AddScoped<IHasAccessList>(ctx => ctx.Resolve<IBeaconBlockRootHandler>())
                // Chains may bind their own IBlockhashStore; only hint-capable stores contribute.
                .AddScoped<IHasAccessList>(ctx => ctx.Resolve<IBlockhashStore>() as IHasAccessList ?? NoAccessList.Instance)

                .Add<PrewarmerEnvFactory>()

                .AddDecorator<IWorldStateScopeProvider>((ctx, worldStateScopeProvider) =>
                {
                    if (worldStateScopeProvider is PrewarmerScopeProvider) return worldStateScopeProvider; // Inner world state
                    return new PrewarmerScopeProvider(
                        worldStateScopeProvider,
                        ctx.Resolve<IPrewarmerState>(),
                        ctx.Resolve<ILogManager>()
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
                        blocksConfig.CachePrecompilesOnBlockProcessing ? preBlockCaches : null);
                })

                .AddDecorator<ITransactionProcessorAdapter, PrewarmerTxAdapter>();

            if (blocksConfig.PreWarming == PreWarmMode.BlockAndMempool)
            {
                // Shares the scoped IBlockCachePreWarmer / PreBlockCaches with the main processor. Eagerly resolved
                // when the prewarmer is activated so it subscribes to head updates as soon as processing is wired up.
                builder
                    .AddScoped<MempoolStatePrewarmer>()
                    .ResolveOnServiceActivation<MempoolStatePrewarmer, IBlockCachePreWarmer>();
            }
        }

        private sealed class NoAccessList : IHasAccessList
        {
            public static readonly NoAccessList Instance = new();

            public AccessList? GetAccessList(Block block, IReleaseSpec spec) => null;
        }
    }
}
