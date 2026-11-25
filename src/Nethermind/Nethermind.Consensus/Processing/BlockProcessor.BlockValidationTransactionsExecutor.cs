// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public class BlockValidationTransactionsExecutor(
        ITransactionProcessorAdapter transactionProcessor,
        IWorldState stateProvider,
        BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
        : IBlockProcessor.IBlockTransactionsExecutor
    {
        protected IWorldState _stateProvider = stateProvider;
        protected ITransactionProcessedEventHandler? _transactionProcessedEventHandler = transactionProcessedEventHandler;

        private ArrayPoolList<long>? _txTimings;

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);

        public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            _txTimings = PerTxTimingCollector.Prepare(block.Transactions.Length);
            try
            {
                bool shouldValidate = !processingOptions.ContainsFlag(ProcessingOptions.NoValidation);

                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    Transaction currentTx = block.Transactions[i];

                    ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);

                    if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)
                    {
                        ThrowInvalidBlockForGasLimit(block);
                    }
                }

                return [.. receiptsTracer.TxReceipts];
            }
            finally
            {
                // The collector snapshot owns the list after the block, so the executor must not keep it.
                _txTimings = null;
            }

            [DebuggerHidden]
            [DoesNotReturn]
            static void ThrowInvalidBlockForGasLimit(Block block) => throw new InvalidBlockException(block, Core.Messages.BlockErrorMessages.ExceededGasLimit);
        }

        protected virtual void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
        {
            long txStart = PerTxTimingCollector.Start(_txTimings);
            TransactionResult result;
            try
            {
                result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _stateProvider);
            }
            finally
            {
                // Stop the timer even on failure so a slow-block log captures the failing tx's time
                PerTxTimingCollector.Stop(_txTimings, index, txStart);
            }
            if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
            if (_transactionProcessedEventHandler is { HasSubscribers: true } handler)
                handler.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
        }

        [DoesNotReturn, StackTraceHidden]
        internal static void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction currentTx, int index) => throw new InvalidTransactionException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}", result);

        /// <summary>
        /// Receives each transaction processed on the main chain, raised as <see cref="IMainProcessingContext.TransactionProcessed"/>.
        /// </summary>
        public interface ITransactionProcessedEventHandler
        {
            void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs);

            /// <summary>Whether anything observes processed transactions; when not, callers skip building the event.</summary>
            bool HasSubscribers => true;
        }
    }
}
