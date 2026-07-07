// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>Sequential BAL env: executes against the mutable state provider directly.</summary>
/// <remarks><paramref name="lifetimeScope"/> is the owning DI scope (Autofac path) disposed with
/// the env, or <c>null</c> when built manually.</remarks>
public sealed class SequentialBalEnv(
    TracedAccessWorldState worldState,
    ITransactionProcessor txProcessor,
    ITransactionProcessorAdapter txProcessorAdapter,
    IWithdrawalProcessor withdrawalProcessor,
    ILifetimeScope? lifetimeScope = null) : IBalProcessingEnv
{
    public TracedAccessWorldState WorldState { get; } = worldState;
    public ITransactionProcessor TxProcessor { get; } = txProcessor;
    public ITransactionProcessorAdapter TxProcessorAdapter { get; } = txProcessorAdapter;
    public IWithdrawalProcessor WithdrawalProcessor { get; } = withdrawalProcessor;

    public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParallelBalEnvManager.ParentReaderLease? parentReader)
    {
        WorldState.Clear();
        WorldState.SetIndex(balIndex);
        TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
    }

    public void ClearParentReader() { }

    public void Dispose() => lifetimeScope?.Dispose();
}
