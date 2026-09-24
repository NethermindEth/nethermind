// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
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
        public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) =>
            Wrap(worldState.TryBeginScope(baseBlock, out IDisposable? closer), closer, out scope);

        public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) =>
            Wrap(worldState.TryBeginScopeAtTarget(targetBlock, out IDisposable? closer), closer, out scope);

        private bool Wrap(bool acquired, IDisposable? closer, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
        {
            scope = acquired ? new ReadOnlyTxProcessingScope(transactionProcessor, closer!, worldState) : null;
            return acquired;
        }

        public void Dispose() => lifetimeScope.Dispose();
    }
}
