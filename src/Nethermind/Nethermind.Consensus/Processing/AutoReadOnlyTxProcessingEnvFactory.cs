// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class AutoReadOnlyTxProcessingEnvFactory(ILifetimeScope parentLifetime, IWorldStateManager worldStateManager) : IReadOnlyTxProcessingEnvFactory
{
    public IReadOnlyTxProcessorSource Create()
    {
        IVisitingWorldState worldState = worldStateManager.CreateResettableWorldState();
        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IVisitingWorldState>(worldState).AddSingleton<IWorldState>()
                .AddSingleton<AutoReadOnlyTxProcessingEnv>();
        });

        return childScope.Resolve<AutoReadOnlyTxProcessingEnv>();
    }

    public IReadOnlyTxProcessorSource CreateForWarmingUp(IWorldState worldStateToWarmUp)
    {
        IVisitingWorldState worldState = worldStateManager.CreateWorldStateForWarmingUp(worldStateToWarmUp);
        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IVisitingWorldState>(worldState).AddSingleton<IWorldState>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnv>();
        });

        return childScope.Resolve<AutoReadOnlyTxProcessingEnv>();
    }

    private class AutoReadOnlyTxProcessingEnv(ITransactionProcessor transactionProcessor, IVisitingWorldState worldState, ILifetimeScope lifetimeScope) : IReadOnlyTxProcessorSource, IDisposable
    {
        public IReadOnlyTxProcessingScope Build(Hash256 stateRoot)
        {
            Hash256 originalStateRoot = worldState.StateRoot;
            worldState.StateRoot = stateRoot;
            return new ReadOnlyTxProcessingScope(transactionProcessor, worldState, originalStateRoot);
        }

        public void Dispose()
        {
            lifetimeScope.Dispose();
        }
    }
}
