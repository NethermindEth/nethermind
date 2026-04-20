// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Metrics = Nethermind.Evm.Metrics;

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

        public virtual void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);

        public virtual void SetBlockAccessListManager(in IBlockAccessListManager balManager) { }

        public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            Metrics.ResetBlockStats();

            bool shouldValidate = !processingOptions.ContainsFlag(ProcessingOptions.NoValidation);

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction currentTx = block.Transactions[i];
                ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);

                if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)
                {
                    ThrowInvalidBlockForGasLimit(block);
                }
            }

            return [.. receiptsTracer.TxReceipts];

            [DebuggerHidden]
            [DoesNotReturn]
            static void ThrowInvalidBlockForGasLimit(Block block) => throw new InvalidBlockException(block, Core.Messages.BlockErrorMessages.ExceededGasLimit);
        }

        private void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            => ProcessTransaction(transactionProcessor, _stateProvider, block, currentTx, index, receiptsTracer, processingOptions, _transactionProcessedEventHandler);

        protected virtual void ProcessTransaction(
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider,
            Block block,
            Transaction currentTx,
            int index,
            BlockReceiptsTracer receiptsTracer,
            ProcessingOptions processingOptions,
            ITransactionProcessedEventHandler? transactionProcessedEventHandler)
        {
            TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
            if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
            _transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
        }

        [DoesNotReturn, StackTraceHidden]
        protected static void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction currentTx, int index) => throw new InvalidTransactionException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}", result);

        /// <summary>
        /// Used by <see cref="FilterManager"/> through <see cref="IMainProcessingContext"/>
        /// </summary>
        public interface ITransactionProcessedEventHandler
        {
            void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs);
        }
    }
}
