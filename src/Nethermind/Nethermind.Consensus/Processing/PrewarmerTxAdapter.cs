// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Coordinates cache handoff and reports the main thread's per-transaction progress to the prewarmer.
/// The <see cref="IPrewarmerState.IsPrewarmer"/> guard ensures only the main execution reports, not the prewarmer's own scope.
/// </summary>
public class PrewarmerTxAdapter(ITransactionProcessorAdapter baseAdapter, BlockCachePreWarmer preWarmer, IPrewarmerState prewarmerState) : ITransactionProcessorAdapter
{
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        ReportProgress(transaction);
        return baseAdapter.Execute(transaction, txTracer);
    }

    private void ReportProgress(Transaction transaction)
    {
        if (!prewarmerState.IsPrewarmer)
        {
            preWarmer.OnBeforeTxExecution(transaction);
        }
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => baseAdapter.SetBlockExecutionContext(in blockExecutionContext);
}
