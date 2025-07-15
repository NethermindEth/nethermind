// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockValidationTransactionsExecutor(
    IBlockProcessor.IBlockTransactionsExecutor baseTransactionExecutor,
    SimulateRequestState simulateState)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    private IReleaseSpec _spec;
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _spec = blockExecutionContext.Spec;
        baseTransactionExecutor.SetBlockExecutionContext(in blockExecutionContext);
    }

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer,
        CancellationToken token = default)
    {
        if (!simulateState.Validate)
        {
            processingOptions |= ProcessingOptions.ForceProcessing | ProcessingOptions.DoNotVerifyNonce | ProcessingOptions.NoValidation;
        }

        if (simulateState.BlobBaseFeeOverride is not null)
        {
            SetBlockExecutionContext(new BlockExecutionContext(block.Header, _spec, simulateState.BlobBaseFeeOverride.Value));
        }

        return baseTransactionExecutor.ProcessTransactions(block, processingOptions, receiptsTracer, token);
    }

    public bool IsTransactionInBlock(Transaction tx)
    {
        throw new NotImplementedException();
    }

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed
    {
        add => baseTransactionExecutor.TransactionProcessed += value;
        remove => baseTransactionExecutor.TransactionProcessed -= value;
    }
}
