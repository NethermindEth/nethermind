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
    /// <summary>Builds one parallel-execution env intended to be pooled per worker for a block.</summary>
    /// <remarks>Must not wrap in a populating prewarmer — its writes to the shared PreBlockCaches.StateCache would shadow MVMM and poison later txs.</remarks>
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
                // Parallel-mode TxProcessor: no header.GasUsed mutation, lets us share block.Header across workers.
                .AddScoped<ITransactionProcessor, EthereumParallelTransactionProcessor>()
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

        /// <summary>Targets this env at the given tx version for the next Build + Execute on the owning worker.</summary>
        public void SetTxVersion(in TxVersion version)
        {
            WorldStateScopeProvider.SetTxVersion(in version);
            FeeRecorder.SetTxIndex(version.TxIndex);
        }
    }
}
