// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <param name="cacheCode">When false the env keeps deposited code out of the process-wide
/// <see cref="ICodeCache"/>, which nothing journals, so a rolled-back deposit cannot outlive its scope.</param>
public class AutoReadOnlyTxProcessingEnvFactory(ILifetimeScope parentLifetime, IWorldStateManager worldStateManager, ISpecProvider specProvider, bool cacheCode = true) : IReadOnlyTxProcessingEnvFactory
{
    public IReadOnlyTxProcessorSource Create()
    {
        IWorldStateScopeProvider worldState = worldStateManager.CreateResettableWorldState();
        // Mempool admission and the parallel BAL parent readers share these envs, so only add a recorder
        // where a diff can actually be read.
        IReleaseSpec finalSpec = specProvider.GetFinalSpec();
        bool recordsTransactionDiffs = finalSpec.IsEip7906Enabled && finalSpec.BlockLevelAccessListsEnabled;
        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnv>();
            if (!cacheCode)
            {
                builder.AddSingleton<ICodeCache>(NoopCodeCache.Instance);
            }

            if (recordsTransactionDiffs)
            {
                // At scope level so the tx processor and the code repository share one slice.
                builder.AddDecorator<IWorldState>(static (_, inner) => new TracedAccessWorldState(inner, parallel: false));
            }
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

        public void Dispose() => lifetimeScope.Dispose();
    }
}
