// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockValidationTransactionsExecutor(
            IWorldState stateProvider,
            ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ISpecProvider specProvider,
            IVirtualMachine virtualMachine,
            ICodeInfoRepository codeInfoRepository,
            ILogManager logManager,
            BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
            : IBlockProcessor.IBlockTransactionsExecutor
        {
            private readonly TracedAccessWorldState? _tracedAccessWorldState = stateProvider as TracedAccessWorldState;
            private ITransactionProcessorAdapter? _transactionProcessor;
            private BlockExecutionContext _blockExecutionContext;

            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            {
                _transactionProcessor?.SetBlockExecutionContext(in blockExecutionContext);
                _blockExecutionContext = blockExecutionContext;
            }

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
            {
                Metrics.ResetBlockStats();

                int len = block.Transactions.Length;
                if (_tracedAccessWorldState.ParallelExecutionEnabled)
                {
                        var transactionProcessors = new ITransactionProcessorAdapter[len];
                        for (int i = 0; i < len; i++)
                        {
                        TransactionProcessor transactionProcessor = new(blobBaseFeeCalculator, specProvider, stateProvider, virtualMachine, codeInfoRepository, logManager);
                        ExecuteTransactionProcessorAdapter transactionProcessorAdapter = new(transactionProcessor);
                        transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);
                        transactionProcessors[i] = transactionProcessorAdapter;
                    }

                    ParallelUnbalancedWork.For(
                        0,
                        block.Transactions.Length,
                        ParallelUnbalancedWork.DefaultOptions,
                        (block, receiptsTracer, processingOptions, stateProvider, transactionProcessors, txs: block.Transactions),
                        static (i, state) =>
                    {
                        Transaction tx = state.txs[i];
                        ITransactionProcessorAdapter transactionProcessor = state.transactionProcessors[i];
                        TracedAccessWorldState worldState = (state.stateProvider as TracedAccessWorldState)!;
                        ProcessTransactionParallel(
                            transactionProcessor,
                            state.stateProvider,
                            state.block,
                            tx,
                            i,
                            state.receiptsTracer,
                            state.processingOptions);
                        return state;
                    });

                    // for (int i = 0; i < len; i++)
                    // {
                    //     TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, block.Transactions[i], receiptsTracer.TxReceipts[i]));
                    // }
                }
                else
                {
                    if (_transactionProcessor is null)
                    {
                        TransactionProcessor transactionProcessor = new(blobBaseFeeCalculator, specProvider, stateProvider, virtualMachine, codeInfoRepository, logManager);
                        _transactionProcessor = new ExecuteTransactionProcessorAdapter(transactionProcessor);
                    }

                    for (int i = 0; i < block.Transactions.Length; i++)
                    {
                        _tracedAccessWorldState?.GeneratedBlockAccessList.IncrementBlockAccessIndex();
                        Transaction currentTx = block.Transactions[i];
                        ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
                    }
                    _tracedAccessWorldState?.GeneratedBlockAccessList.IncrementBlockAccessIndex();
                }

                return [.. receiptsTracer.TxReceipts];
            }

            protected virtual void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            {
                TransactionResult result = _transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
                if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
                transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
            }

            private static void ProcessTransactionParallel(
                ITransactionProcessorAdapter transactionProcessor,
                IWorldState stateProvider,
                Block block,
                Transaction currentTx,
                int index,
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions)
            {
                // todo: parallelise tracers
                TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
                if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction currentTx, int index)
            {
                throw new InvalidTransactionException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}", result);
            }

            /// <summary>
            /// Used by <see cref="FilterManager"/> through <see cref="IMainProcessingContext"/>
            /// </summary>
            public interface ITransactionProcessedEventHandler
            {
                void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs);
            }
        }
    }
}
