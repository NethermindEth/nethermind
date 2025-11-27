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
        var worldState = new PrewarmerScopeProvider(
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

        return childScope.Resolve<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>();
    }
}
