// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

public class ParallelEnvFactory(IWorldStateManager worldStateManager, ILifetimeScope parentLifetime)
{
    public ParallelAutoReadOnlyTxProcessingEnv Create(TxVersion version, MultiVersionMemory multiVersionMemory, FeeAccumulator feeAccumulator, IReleaseSpec spec)
    {
        // Do NOT wrap the per-tx resettable scope in a populating prewarmer here. That
        // prewarmer would write every fresh read into the SHARED PreBlockCaches.StateCache,
        // poisoning subsequent txs whose outer (read-only) prewarmer would then return that
        // stale value without consulting MultiVersionMemory. The outer prewarmer in the DI
        // chain provides block-level caching; per-tx reads must go directly through MVMM.
        MultiVersionMemoryScopeProvider worldState = new(
            version,
            worldStateManager.CreateResettableWorldState(),
            multiVersionMemory,
            feeAccumulator
        );

        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope(builder =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<MultiVersionMemoryScopeProvider>(worldState)
                .AddSingleton<IFeeRecorder>(ctx => new ParallelFeeRecorder(version.TxIndex, feeAccumulator, worldState, ctx.Resolve<IWorldState>(), spec))
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
