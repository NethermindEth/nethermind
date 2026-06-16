// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Diagnostics;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public class BlockValidationTransactionsExecutor(
        ITransactionProcessorAdapter transactionProcessor,
        IWorldState stateProvider,
        BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null,
        ILogManager? logManager = null)
        : IBlockProcessor.IBlockTransactionsExecutor
    {
        protected IWorldState _stateProvider = stateProvider;
        protected ITransactionProcessedEventHandler? _transactionProcessedEventHandler = transactionProcessedEventHandler;

        // DIAGNOSTIC: verbose per-tx execution logging to correlate against prewarmer activity.
        private readonly ILogger _logger = logManager?.GetClassLogger<BlockValidationTransactionsExecutor>() ?? NullLogger.Instance;
        // DIAGNOSTIC: running totals of main-path prewarm-cache coverage, snapshotted per block.
        private long _prevSlotHit, _prevSlotMiss, _prevAddrHit, _prevAddrMiss;

        public virtual void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);

        public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            Metrics.ResetBlockStats();

            bool shouldValidate = !processingOptions.ContainsFlag(ProcessingOptions.NoValidation);
            bool diag = _logger.IsInfo;
            if (diag) PrewarmCoverage.Enabled = true;
            bool throttling = PrewarmThrottle.Enabled;

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction currentTx = block.Transactions[i];
                if (diag) PrewarmDiag.Record(PrewarmDiag.KindExec, block.Number, i);
                ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
                if (throttling) PrewarmThrottle.OnExecuted();

                if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)
                {
                    ThrowInvalidBlockForGasLimit(block);
                }
            }

            if (diag)
            {
                // DIAGNOSTIC: drain recorded prewarm/exec events off-thread so per-block timing is undisturbed.
                ThreadPool.UnsafeQueueUserWorkItem(static (ILogger l) => PrewarmDiag.Flush(l), _logger, preferLocal: false);
                LogCoverageDelta(block.Number);
            }

            return [.. receiptsTracer.TxReceipts];

            [DebuggerHidden]
            [DoesNotReturn]
            static void ThrowInvalidBlockForGasLimit(Block block) => throw new InvalidBlockException(block, Core.Messages.BlockErrorMessages.ExceededGasLimit);
        }

        // DIAGNOSTIC: emit this block's main-path prewarm-cache coverage (one line/block, off the per-tx path).
        private void LogCoverageDelta(long blockNumber)
        {
            long sh = PrewarmCoverage.SlotHit, sm = PrewarmCoverage.SlotMiss, ah = PrewarmCoverage.AddrHit, am = PrewarmCoverage.AddrMiss;
            long dsh = sh - _prevSlotHit, dsm = sm - _prevSlotMiss, dah = ah - _prevAddrHit, dam = am - _prevAddrMiss;
            _prevSlotHit = sh; _prevSlotMiss = sm; _prevAddrHit = ah; _prevAddrMiss = am;
            _logger.Info($"Block {blockNumber} prewarm-coverage: slot_hit={dsh} slot_miss={dsm} addr_hit={dah} addr_miss={dam}");
        }

        protected virtual void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
        {
            TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _stateProvider);
            if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
            _transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
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
