// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Clears the <see cref="ResolvedCodeMemo"/> after every transaction that leaves its changes in the state.
/// </summary>
/// <remarks>
/// A restored transaction leaves every address's code as it found it, so the memo still holds for the next
/// transaction of the scope, as for the re-runs of eth_estimateGas and eth_createAccessList. A transaction that keeps
/// its changes can remove code the memo holds, as SELFDESTRUCT does at its end, so the next one starts empty.
/// </remarks>
public sealed class ResolvedCodeClearingTransactionProcessor(ITransactionProcessor transactionProcessor, ResolvedCodeMemo memo) : ITransactionProcessor
{
    public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options)
    {
        try
        {
            return transactionProcessor.Process(transaction, txTracer, options);
        }
        finally
        {
            if (!options.HasFlag(ExecutionOptions.Restore)) memo.Clear();
        }
    }

    public void SetBlockExecutionContext(BlockHeader blockHeader) =>
        transactionProcessor.SetBlockExecutionContext(blockHeader);

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
}
