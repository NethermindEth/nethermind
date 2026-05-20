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

    public interface IBlobBaseFeeCalculator
    {
        bool TryCalculateBlobBaseFee(BlockHeader header, Transaction transaction,
            UInt256 blobGasPriceUpdateFraction, out UInt256 blobBaseFee);
    }
}

public static class ITransactionProcessorExtensions
{
    extension(ITransactionProcessor transactionProcessor)
    {
        /// <summary>
        /// Execute transaction, commit state.
        /// </summary>
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
            => transactionProcessor.Process(transaction, txTracer, ExecutionOptions.Commit);

        /// <summary>
        /// Call transaction, rollback state.
        /// </summary>
        public TransactionResult CallAndRestore(Transaction transaction, ITxTracer txTracer)
            => transactionProcessor.Process(transaction, txTracer, ExecutionOptions.CommitAndRestore);

        /// <summary>
        /// Execute transaction, keep the state uncommitted (block-building mode).
        /// </summary>
        public TransactionResult BuildUp(Transaction transaction, ITxTracer txTracer)
            => transactionProcessor.Process(transaction, txTracer, ExecutionOptions.BuildUp);

        /// <summary>
        /// Call transaction, no validations, commit state.
        /// Will NOT charge gas from sender account, so stateDiff will miss gas fee.
        /// </summary>
        public TransactionResult Trace(Transaction transaction, ITxTracer txTracer)
            => transactionProcessor.Process(transaction, txTracer, ExecutionOptions.SkipValidationAndCommit);

        /// <summary>
        /// Call transaction, no validations, don't commit state.
        /// Will NOT charge gas from sender account.
        /// </summary>
        public TransactionResult Warmup(Transaction transaction, ITxTracer txTracer)
            => transactionProcessor.Process(transaction, txTracer, ExecutionOptions.Warmup | ExecutionOptions.SkipValidation);

        public TransactionResult Execute(Transaction transaction, BlockHeader header, ITxTracer txTracer)
        {
            transactionProcessor.SetBlockExecutionContext(header);
            return transactionProcessor.Execute(transaction, txTracer);
        }

        public TransactionResult CallAndRestore(Transaction transaction, BlockHeader header, ITxTracer txTracer)
        {
            transactionProcessor.SetBlockExecutionContext(header);
            return transactionProcessor.CallAndRestore(transaction, txTracer);
        }

        public TransactionResult Execute(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
        {
            transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
            return transactionProcessor.Execute(transaction, txTracer);
        }

        public TransactionResult CallAndRestore(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
        {
            transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
            return transactionProcessor.CallAndRestore(transaction, txTracer);
        }

        public TransactionResult BuildUp(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
        {
            transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
            return transactionProcessor.BuildUp(transaction, txTracer);
        }

        public TransactionResult Trace(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
        {
            transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
            return transactionProcessor.Trace(transaction, txTracer);
        }

        public TransactionResult Warmup(Transaction transaction, in BlockExecutionContext blockExecutionContext, ITxTracer txTracer)
        {
            transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
            return transactionProcessor.Warmup(transaction, txTracer);
        }
    }
}
