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
        IWorldState worldState = worldStateManager.CreateResettableWorldState();
        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IWorldState>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnv>();
        });

        return childScope.Resolve<AutoReadOnlyTxProcessingEnv>();
    }

    public IReadOnlyTxProcessorSource CreateForWarmingUp(IWorldState worldStateToWarmUp)
    {
        IWorldState worldState = worldStateManager.CreateWorldStateForWarmingUp(worldStateToWarmUp);
        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IWorldState>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnv>();
        });

        return childScope.Resolve<AutoReadOnlyTxProcessingEnv>();
    }

    private class AutoReadOnlyTxProcessingEnv(ITransactionProcessor transactionProcessor, IWorldState worldState, ILifetimeScope lifetimeScope) : IReadOnlyTxProcessorSource, IDisposable
    {
        public IReadOnlyTxProcessingScope Build(BlockHeader? header)
        {
            worldState.SetBaseBlock(header);
            return new ReadOnlyTxProcessingScope(transactionProcessor, worldState);
        }

        public void Dispose()
        {
            lifetimeScope.Dispose();
        }
    }
}
