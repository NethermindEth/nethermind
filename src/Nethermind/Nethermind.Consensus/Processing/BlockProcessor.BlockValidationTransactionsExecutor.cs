// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.State;
using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockValidationTransactionsExecutor(
            ISpecProvider specProvider,
            IVirtualMachine virtualMachine,
            ICodeInfoRepository codeInfoRepository,
            IWorldState stateProvider,
            ILogManager logManager) : IBlockProcessor.IBlockTransactionsExecutor
        {
            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
            private BlockExecutionContext _blockExecutionContext;
            private readonly ISpecProvider _specProvider = specProvider;
            private readonly IVirtualMachine _virtualMachine = virtualMachine;
            private readonly ICodeInfoRepository _codeInfoRepository = codeInfoRepository;
            private readonly IWorldState _stateProvider = stateProvider;
            private readonly ILogManager _logManager = logManager;
            private ITransactionProcessorAdapter? _transactionProcessor;

            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            {
                _transactionProcessor?.SetBlockExecutionContext(in blockExecutionContext);
                _blockExecutionContext = blockExecutionContext;
            }

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
            {
                Metrics.ResetBlockStats();

                int len = block.Transactions.Length;
                if (block.DecodedBlockAccessList is null)
                {
                    if (_transactionProcessor is null)
                    {
                        TransactionProcessor transactionProcessor = new(_specProvider, _stateProvider, _virtualMachine, _codeInfoRepository, _logManager);
                        _transactionProcessor = new ExecuteTransactionProcessorAdapter(transactionProcessor);
                    }
                    for (int i = 0; i < len; i++)
                    {
                        block.TransactionProcessed = i;
                        Transaction currentTx = block.Transactions[i];
                        ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
                    }
                }
                else
                {
                    var stateProviders = new BlockAccessWorldState[len];
                    var transactionProcessors = new ITransactionProcessorAdapter[len];
                    for (int i = 0; i < len; i++)
                    {
                        // clone world state
                        BlockAccessWorldState blockAccessStateProvider = new(block.DecodedBlockAccessList!.Value, (ushort)(i + 1), _stateProvider);
                        TransactionProcessor transactionProcessor = new(_specProvider, blockAccessStateProvider, _virtualMachine, _codeInfoRepository, _logManager);
                        ExecuteTransactionProcessorAdapter transactionProcessorAdapter = new(transactionProcessor);
                        transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);
                        stateProviders[i] = blockAccessStateProvider;
                        transactionProcessors[i] = transactionProcessorAdapter;
                    }

                    ParallelUnbalancedWork.For(
                        0,
                        block.Transactions.Length,
                        ParallelUnbalancedWork.DefaultOptions,
                        (block, receiptsTracer, processingOptions, stateProviders, transactionProcessors, txs: block.Transactions),
                        static (i, state) =>
                    {
                        Transaction tx = state.txs[i];
                        ProcessTransactionParallel(
                            state.transactionProcessors[i],
                            state.stateProviders[i],
                            state.block,
                            tx,
                            i,
                            state.receiptsTracer,
                            state.processingOptions);
                        return state;
                    });

                    for (int i = 0; i < len; i++)
                    {
                        TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, block.Transactions[i], receiptsTracer.TxReceipts[i]));
                    }
                }

                return [.. receiptsTracer.TxReceipts];
            }

            protected virtual void ProcessTransaction(Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            {
                TransactionResult result = _transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _stateProvider);
                if (!result) ThrowInvalidBlockException(result, block.Header, currentTx, index);
                TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
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
                if (!result) ThrowInvalidBlockException(result, block.Header, currentTx, index);
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowInvalidBlockException(TransactionResult result, BlockHeader header, Transaction currentTx, int index)
            {
                throw new InvalidBlockException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.Error}");
            }
        }
    }
}
