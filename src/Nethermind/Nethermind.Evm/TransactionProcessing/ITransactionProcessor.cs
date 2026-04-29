// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm.TransactionProcessing;

public interface ITransactionProcessor
{
    TransactionResult Process(
        Transaction transaction,
        ITxTracer txTracer,
        ExecutionOptions options);

    void SetBlockExecutionContext(BlockHeader blockHeader);
    void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext);

    /// <summary>
    /// Call transaction, no validations, don't commit state
    /// Will NOT charge gas from sender account
    /// </summary>
    TransactionResult Warmup(Transaction transaction, ITxTracer txTracer)
        => Process(transaction, txTracer, ExecutionOptions.Warmup | ExecutionOptions.SkipValidation);

    public interface IBlobBaseFeeCalculator
    {
        bool TryCalculateBlobBaseFee(BlockHeader header, Transaction transaction,
            UInt256 blobGasPriceUpdateFraction, out UInt256 blobBaseFee);
    }
}

public static class ITransactionProcessorExtensions
{
    /// <summary>
    /// Execute transaction, commit state.
    /// </summary>
    public static TransactionResult Execute(
        this ITransactionProcessor transactionProcessor,
        Transaction transaction,
        ITxTracer txTracer)
        => transactionProcessor.Process(
            transaction,
            txTracer,
            ExecutionOptions.Commit);

    /// <summary>
    /// Call transaction, rollback state.
    /// </summary>
    public static TransactionResult CallAndRestore(
        this ITransactionProcessor transactionProcessor,
        Transaction transaction,
        ITxTracer txTracer)
        => transactionProcessor.Process(
            transaction,
            txTracer,
            ExecutionOptions.CommitAndRestore);

    /// <summary>
    /// Execute transaction, keep the state uncommitted (block-building mode).
    /// </summary>
    public static TransactionResult BuildUp(
        this ITransactionProcessor transactionProcessor,
        Transaction transaction,
        ITxTracer txTracer)
        => transactionProcessor.Process(
            transaction,
            txTracer,
            ExecutionOptions.None);


    /// <summary>
    /// Call transaction, no validations, commit state.
    /// Will NOT charge gas from sender account, so stateDiff will miss gas fee.
    /// </summary>
    public static TransactionResult Trace(
        this ITransactionProcessor transactionProcessor,
        Transaction transaction,
        ITxTracer txTracer)
        => transactionProcessor.Process(
            transaction,
            txTracer,
            ExecutionOptions.SkipValidationAndCommit);

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
