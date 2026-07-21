// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class PrewarmerEnvFactory(IWorldStateManager worldStateManager, ILogManager logManager, ILifetimeScope parentLifetime)
{
    public IReadOnlyTxProcessorSource Create(PreBlockCaches preBlockCaches)
    {
        PrewarmerState prewarmerState = new(preBlockCaches, isPrewarmer: true);
        PrewarmerScopeProvider worldState = new(
            worldStateManager.CreateResettableWorldState(),
            prewarmerState,
            logManager
        );

        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IPrewarmerState>(prewarmerState)
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>();
        });

        return childScope.Resolve<AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv>();
    }
}
