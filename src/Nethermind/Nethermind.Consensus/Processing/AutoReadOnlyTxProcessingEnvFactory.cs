// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class AutoReadOnlyTxProcessingEnvFactory(ILifetimeScope parentLifetime, IWorldStateManager worldStateManager, ISpecProvider specProvider) : IReadOnlyTxProcessingEnvFactory
{
    public IReadOnlyTxProcessorSource Create()
    {
        IWorldStateScopeProvider worldState = worldStateManager.CreateResettableWorldState();
        // These envs also back mempool admission and the parallel block-access-list parent readers, so the
        // EIP-7906 diff recorder is only worth a layer on a chain that ever records a diff to read.
        IReleaseSpec finalSpec = specProvider.GetFinalSpec();
        bool recordsTransactionDiffs = finalSpec.IsEip7906Enabled && finalSpec.BlockLevelAccessListsEnabled;
        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope((builder) =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<AutoReadOnlyTxProcessingEnv>();
            if (recordsTransactionDiffs)
            {
                // Idle until a transaction that reads its own diff switches it on, so that when one does,
                // the whole stack - tx processor and code repository alike - shares a single slice.
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
