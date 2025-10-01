// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm.TransactionProcessing;

public interface ITransactionProcessor
{
    /// <summary>
    /// Execute transaction, commit state
    /// </summary>
    TransactionResult Execute(Transaction transaction, ITxTracer txTracer, bool? isFromTraceEndpoint = null);

    /// <summary>
    /// Call transaction, rollback state
    /// </summary>
    TransactionResult CallAndRestore(Transaction transaction, ITxTracer txTracer);

    /// <summary>
    /// Execute transaction, keep the state uncommitted
    /// </summary>
    TransactionResult BuildUp(Transaction transaction, ITxTracer txTracer);

    /// <summary>
    /// Call transaction, no validations, commit state
    /// Will NOT charge gas from sender account, so stateDiff will miss gas fee
    /// </summary>
    TransactionResult Trace(Transaction transaction, ITxTracer txTracer);

    /// <summary>
    /// Call transaction, no validations, don't commit state
    /// Will NOT charge gas from sender account
    /// </summary>
    TransactionResult Warmup(Transaction transaction, ITxTracer txTracer);


    void SetBlockExecutionContext(BlockHeader blockHeader);
    void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);

    public interface IBlobBaseFeeCalculator
    {
        bool TryCalculateBlobBaseFee(BlockHeader header, Transaction transaction,
            UInt256 blobGasPriceUpdateFraction, out UInt256 blobBaseFee);
    }
}

public static class ITransactionProcessorExtensions
{
    public static TransactionResult Execute(this ITransactionProcessor transactionProcessor, Transaction transaction, BlockHeader header, ITxTracer txTracer)
    {
        transactionProcessor.SetBlockExecutionContext(header);
        return transactionProcessor.Execute(transaction, txTracer);
    }

    public static TransactionResult CallAndRestore(this ITransactionProcessor transactionProcessor, Transaction transaction, BlockHeader header, ITxTracer txTracer)
    {
        transactionProcessor.SetBlockExecutionContext(header);
        return transactionProcessor.CallAndRestore(transaction, txTracer);
    }

    public static TransactionResult Execute(this ITransactionProcessor transactionProcessor, Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
        return transactionProcessor.Execute(transaction, txTracer);
    }

    public static TransactionResult CallAndRestore(this ITransactionProcessor transactionProcessor, Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
        return transactionProcessor.CallAndRestore(transaction, txTracer);
    }

    public static TransactionResult BuildUp(this ITransactionProcessor transactionProcessor, Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
        return transactionProcessor.BuildUp(transaction, txTracer);
    }
    public static TransactionResult Trace(this ITransactionProcessor transactionProcessor, Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
        return transactionProcessor.Trace(transaction, txTracer);
    }

    public static TransactionResult Warmup(this ITransactionProcessor transactionProcessor, Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
    {
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
        return transactionProcessor.Warmup(transaction, txTracer);
    }
}
