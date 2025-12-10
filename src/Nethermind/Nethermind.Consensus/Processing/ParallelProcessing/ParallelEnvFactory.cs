// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelEnvFactory(IWorldStateManager worldStateManager, ILifetimeScope parentLifetime)
{
    public ParallelAutoReadOnlyTxProcessingEnv Create(Version version, MultiVersionMemory multiVersionMemory, PreBlockCaches preBlockCaches)
    {
        MultiVersionMemoryScopeProvider worldState = new(
            version,
            new PrewarmerScopeProvider(worldStateManager.CreateResettableWorldState(),
                preBlockCaches,
                populatePreBlockCache: true),
            multiVersionMemory
        );

        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope(builder =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<MultiVersionMemoryScopeProvider>(worldState)
                .AddSingleton<ParallelAutoReadOnlyTxProcessingEnv>();
        });

        return childScope.Resolve<ParallelAutoReadOnlyTxProcessingEnv>();
    }

    public class ParallelAutoReadOnlyTxProcessingEnv(
        ITransactionProcessor transactionProcessor,
        IWorldState worldState,
        ILifetimeScope lifetimeScope,
        MultiVersionMemoryScopeProvider worldStateScopeProvider)
        : AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv(transactionProcessor, worldState, lifetimeScope)
    {
        public MultiVersionMemoryScopeProvider WorldStateScopeProvider { get; } = worldStateScopeProvider;
    }
}
