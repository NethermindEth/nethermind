// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

public class ParallelEnvFactory(IWorldStateManager worldStateManager, ILifetimeScope parentLifetime, ILogManager logManager)
{
    public ParallelAutoReadOnlyTxProcessingEnv Create(TxVersion version, MultiVersionMemory multiVersionMemory, FeeAccumulator feeAccumulator, PreBlockCaches preBlockCaches)
    {
        // CreateResettableWorldState now returns IWorldStateScopeProvider directly (master
        // moved from IWorldState to scope-provider-typed factories). PrewarmerScopeProvider
        // gained an ILogManager parameter for its detailed-metrics logging path.
        MultiVersionMemoryScopeProvider worldState = new(
            version,
            new PrewarmerScopeProvider(
                worldStateManager.CreateResettableWorldState(),
                preBlockCaches,
                logManager,
                populatePreBlockCache: true),
            multiVersionMemory,
            feeAccumulator
        );

        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope(builder =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<MultiVersionMemoryScopeProvider>(worldState)
                .AddSingleton<IFeeRecorder>(_ => new ParallelFeeRecorder(version.TxIndex, feeAccumulator, worldState))
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
