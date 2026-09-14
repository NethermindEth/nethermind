// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class AutoReadOnlyTxProcessingEnvFactory(ILifetimeScope parentLifetime, IWorldStateManager worldStateManager) : IReadOnlyTxProcessingEnvFactory
{
    public IReadOnlyTxProcessorSource Create()
    {
        IWorldStateScopeProvider worldState = worldStateManager.CreateResettableWorldState();
        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnv>();
        });

        return childScope.Resolve<AutoReadOnlyTxProcessingEnv>();
    }

    public class AutoReadOnlyTxProcessingEnv(ITransactionProcessor transactionProcessor, IWorldState worldState, ILifetimeScope lifetimeScope) : IReadOnlyTxProcessorSource
    {
        public IReadOnlyTxProcessingScope Build(BlockHeader? header)
        {
            IDisposable closer = worldState.BeginScope(header);
            return new ReadOnlyTxProcessingScope(transactionProcessor, closer, worldState);
        }

        public IReadOnlyTxProcessingScope BuildAtTarget(BlockHeader targetBlock) =>
            worldState.TryBeginScope(targetBlock, out IDisposable? closer)
                ? new ReadOnlyTxProcessingScope(transactionProcessor, closer, worldState)
                : throw new InvalidOperationException($"Parent state is unavailable for target block {targetBlock.ToString(BlockHeader.Format.Short)}.");

        public void Dispose() => lifetimeScope.Dispose();
    }
}
