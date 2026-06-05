// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

public class ParallelEnvFactory(IWorldStateManager worldStateManager, ILifetimeScope parentLifetime)
{
    /// <summary>
    /// Builds a reusable parallel-execution env. The returned env is intended to be pooled
    /// per worker for the lifetime of a block: callers set the per-tx context via
    /// <see cref="ParallelAutoReadOnlyTxProcessingEnv.SetTxVersion"/> before each
    /// <see cref="AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv.Build"/>.
    /// </summary>
    /// <remarks>
    /// Do NOT wrap the per-tx resettable scope in a populating prewarmer here. That
    /// prewarmer would write every fresh read into the SHARED PreBlockCaches.StateCache,
    /// poisoning subsequent txs whose outer (read-only) prewarmer would then return that
    /// stale value without consulting MultiVersionMemory. The outer prewarmer in the DI
    /// chain provides block-level caching; per-tx reads must go directly through MVMM.
    /// </remarks>
    public ParallelAutoReadOnlyTxProcessingEnv Create(
        MultiVersionMemory multiVersionMemory,
        FeeAccumulator feeAccumulator,
        ConcurrentDictionary<ValueHash256, byte[]> blockCodeWrites,
        IReleaseSpec spec)
    {
        MultiVersionMemoryScopeProvider worldState = new(
            worldStateManager.CreateResettableWorldState(),
            multiVersionMemory,
            feeAccumulator,
            blockCodeWrites
        );

        ILifetimeScope childScope = parentLifetime.BeginLifetimeScope(builder =>
        {
            builder
                .AddSingleton<IWorldStateScopeProvider>(worldState)
                .AddSingleton<MultiVersionMemoryScopeProvider>(worldState)
                .AddSingleton<ParallelFeeRecorder>(ctx => new ParallelFeeRecorder(feeAccumulator, worldState, ctx.Resolve<IWorldState>(), spec))
                .AddSingleton<IFeeRecorder>(ctx => ctx.Resolve<ParallelFeeRecorder>())
                .AddSingleton<ParallelAutoReadOnlyTxProcessingEnv>();
        });

        return childScope.Resolve<ParallelAutoReadOnlyTxProcessingEnv>();
    }

    public class ParallelAutoReadOnlyTxProcessingEnv(
        ITransactionProcessor transactionProcessor,
        IWorldState worldState,
        ILifetimeScope lifetimeScope,
        MultiVersionMemoryScopeProvider worldStateScopeProvider,
        ParallelFeeRecorder feeRecorder)
        : AutoReadOnlyTxProcessingEnvFactory.AutoReadOnlyTxProcessingEnv(transactionProcessor, worldState, lifetimeScope)
    {
        public MultiVersionMemoryScopeProvider WorldStateScopeProvider { get; } = worldStateScopeProvider;
        public ParallelFeeRecorder FeeRecorder { get; } = feeRecorder;

        /// <summary>
        /// Sets the per-tx context this env runs the next <see cref="Build"/> + Execute
        /// pair against. Must be called from the worker that pooled this env, before
        /// each tx, paired with a single Build/Dispose cycle on the returned scope.
        /// </summary>
        public void SetTxVersion(in TxVersion version)
        {
            WorldStateScopeProvider.SetTxVersion(in version);
            FeeRecorder.SetTxIndex(version.TxIndex);
        }
    }
}
