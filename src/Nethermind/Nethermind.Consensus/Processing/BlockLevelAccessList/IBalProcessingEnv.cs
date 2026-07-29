// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// The per-worker BAL processing environment the pool hands out: a traced world state bundled
/// with the transaction processor (and its adapter, the withdrawal processor, and the
/// execution-requests processor) bound to it.
/// The BAL manager interacts with rented/shared workers only through this surface, never the
/// concrete pool type.
/// </summary>
public interface IBalProcessingEnv : IDisposable
{
    TracedAccessWorldState WorldState { get; }
    ITransactionProcessor TxProcessor { get; }
    ITransactionProcessorAdapter TxProcessorAdapter { get; }
    IWithdrawalProcessor WithdrawalProcessor { get; }
    IExecutionRequestsProcessor ExecutionRequestsProcessor { get; }

    void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParallelBalEnvManager.ParentReaderLease? parentReader);
    void ClearParentReader();
}
