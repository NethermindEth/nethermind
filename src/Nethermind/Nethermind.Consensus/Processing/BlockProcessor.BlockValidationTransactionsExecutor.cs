// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
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
        BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null,
        IStreamedSenderRecovery? senderRecovery = null)
        : IBlockProcessor.IBlockTransactionsExecutor
    {
        protected IWorldState _stateProvider = stateProvider;
        protected ITransactionProcessedEventHandler? _transactionProcessedEventHandler = transactionProcessedEventHandler;

        // Set once per block in SetupTxTimingMetrics on the block-processing thread, then read by
        // parallel workers in StartTxTimer/StopTxTimer. Not volatile: visibility relies on the same
        // ParallelUnbalancedWork.For join barrier that PerTxTimingCollector depends on. See
        // PerTxTimingCollector's <remarks> for the full threading contract.
        private bool _enableTxTimingMetrics;

        public virtual void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);

        public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            Metrics.ResetBlockStats();
            SetupTxTimingMetrics(block);

            bool shouldValidate = !processingOptions.ContainsFlag(ProcessingOptions.NoValidation);

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction currentTx = block.Transactions[i];
                if (currentTx.SenderAddress is null || currentTx.HasAuthorizationList) senderRecovery?.EnsureSenderRecovered(block, currentTx);

                ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);

                if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)
                {
                    ThrowInvalidBlockForGasLimit(block);
                }
            }

            Metrics.SeedBlockGasPriceIfEmpty(block.Header.BaseFeePerGas);
            Metrics.PublishBlockGasPriceGauges();

            return [.. receiptsTracer.TxReceipts];

            [DebuggerHidden]
            [DoesNotReturn]
            static void ThrowInvalidBlockForGasLimit(Block block) => throw new InvalidBlockException(block, Core.Messages.BlockErrorMessages.ExceededGasLimit);
        }

        protected virtual void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
        {
            long txStart = StartTxTimer();
            TransactionResult result;
            try
            {
                result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _stateProvider);
            }
            finally
            {
                // Stop the timer even on failure so a slow-block log captures the failing tx's time
                StopTxTimer(index, txStart);
            }
            if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
            _transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
        }

        public void SetupTxTimingMetrics(Block block)
        {
            // Compile-time switch: when ExecutionMetricsFlag.IsActive folds to false the JIT
            // elides the entire setup path (and the StartTxTimer/StopTxTimer bodies below).
            if (!ExecutionMetricsFlag.IsActive) return;
            _enableTxTimingMetrics = PerTxTimingCollector.IsEnabled;
            if (_enableTxTimingMetrics)
            {
                PerTxTimingCollector.Prepare(block.Transactions.Length);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long StartTxTimer()
        {
            if (!ExecutionMetricsFlag.IsActive) return 0;
            return _enableTxTimingMetrics ? Stopwatch.GetTimestamp() : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StopTxTimer(int i, long txStart)
        {
            if (!ExecutionMetricsFlag.IsActive) return;
            if (_enableTxTimingMetrics)
            {
                PerTxTimingCollector.Record(i, Stopwatch.GetElapsedTime(txStart).Ticks);
            }
        }

        [DoesNotReturn, StackTraceHidden]
        internal static void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction currentTx, int index) => throw new InvalidTransactionException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}", result);

        /// <summary>
        /// Used by <see cref="FilterManager"/> through <see cref="IMainProcessingContext"/>
        /// </summary>
        public interface ITransactionProcessedEventHandler
        {
            void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs);
        }
    }
}
