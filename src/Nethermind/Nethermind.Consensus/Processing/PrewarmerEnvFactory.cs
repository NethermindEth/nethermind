// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class PrewarmerEnvFactory(IWorldStateManager worldStateManager, ILifetimeScope parentLifetime)
{
    public IReadOnlyTxProcessorSource Create(PreBlockCaches preBlockCaches)
    {
        PrewarmerScopeProvider worldState = new(
            worldStateManager.CreateResettableWorldState(),
            preBlockCaches,
            populatePreBlockCache: true
        );

        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>();
        });

        return new PrewarmerTxProcessingEnv(
            childScope.Resolve<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>(),
            worldState);
    }

    private sealed class PrewarmerTxProcessingEnv(
        IReadOnlyTxProcessorSource txProcessingEnv,
        IPreBlockCacheWarmup preBlockCacheWarmup)
        : IReadOnlyTxProcessorSource, IPreBlockCacheWarmupSource
    {
        public IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock) => txProcessingEnv.Build(baseBlock);

        public IPreBlockCacheWarmupSession BuildPreBlockCacheWarmup(BlockHeader? baseBlock)
            => preBlockCacheWarmup.BeginPreBlockCacheWarmup(baseBlock);

        public void Dispose() => txProcessingEnv.Dispose();
    }
}
