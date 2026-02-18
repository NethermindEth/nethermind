// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
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
            ITransactionProcessorAdapter transactionProcessor,
            ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ISpecProvider specProvider,
            // IVirtualMachine virtualMachine,
            IBlockhashProvider blockHashProvider,
            ICodeInfoRepository codeInfoRepository,
            ILogManager logManager,
            BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
            : IBlockProcessor.IBlockTransactionsExecutor
        {
            private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;
            private readonly ITransactionProcessorAdapter _transactionProcessor = transactionProcessor; // system tx exec
            private BlockExecutionContext _blockExecutionContext;

            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            {
                _transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
                _blockExecutionContext = blockExecutionContext;
            }

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
            {
                Metrics.ResetBlockStats();

                int len = block.Transactions.Length;
                if (_balBuilder.ParallelExecutionEnabled)
                {
                    ITransactionProcessorAdapter[] transactionProcessors = new ITransactionProcessorAdapter[len];
                    BlockReceiptsTracer[] receiptsTracers = new BlockReceiptsTracer[len];
                    TaskCompletionSource<long>[] gasResults = new TaskCompletionSource<long>[len];
                    for (int i = 0; i < len; i++)
                    {
                        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
                        TransactionProcessor<EthereumGasPolicy> transactionProcessor = new(blobBaseFeeCalculator, specProvider, stateProvider, virtualMachine, codeInfoRepository, logManager);
                        ExecuteTransactionProcessorAdapter transactionProcessorAdapter = new(transactionProcessor);
                        transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);
                        transactionProcessors[i] = transactionProcessorAdapter;

                        BlockReceiptsTracer tracer = new();
                        tracer.StartNewBlockTrace(block);
                        receiptsTracers[i] = tracer;
                        gasResults[i] = new TaskCompletionSource<long>();
                    }

                    const int GasValidationChunkSize = 8;
                    Task validatorTask = Task.Run(() =>
                    {
                        Console.WriteLine("[parallel] running gas validation");

                        long totalGas = 0;
                        for (int chunkStart = 0; chunkStart < len; chunkStart += GasValidationChunkSize)
                        {
                            int chunkEnd = System.Math.Min(chunkStart + GasValidationChunkSize, len);
                            for (int j = chunkStart; j < chunkEnd; j++)
                                totalGas += gasResults[j].Task.GetAwaiter().GetResult();
                            if (totalGas > block.Header.GasLimit)
                                throw new InvalidBlockException(block, $"Block gas limit exceeded: cumulative gas {totalGas} > block gas limit {block.Header.GasLimit} after transaction index {chunkEnd - 1}.");
                        }
                        _blockExecutionContext.Header.GasUsed = totalGas;
                    });

                    ParallelUnbalancedWork.For(
                        0,
                        len,
                        ParallelUnbalancedWork.DefaultOptions,
                        (block, processingOptions, stateProvider, transactionProcessors, receiptsTracers, gasResults, txs: block.Transactions),
                        static (i, state) =>
                        {
                            ProcessTransactionParallel(
                                state.transactionProcessors[i],
                                state.stateProvider,
                                state.block,
                                state.txs[i],
                                i,
                                state.receiptsTracers[i],
                                state.processingOptions);
                            state.gasResults[i].SetResult(state.txs[i].BlockGasUsed);
                            return state;
                        });

                    validatorTask.GetAwaiter().GetResult();
                    return CombineReceipts(receiptsTracers, len, block);
                }
                else
                {
                    // if (_transactionProcessor is null)
                    // {
                    //     VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
                    //     TransactionProcessor<EthereumGasPolicy> transactionProcessor = new(blobBaseFeeCalculator, specProvider, stateProvider, virtualMachine, codeInfoRepository, logManager);
                    //     _transactionProcessor = new ExecuteTransactionProcessorAdapter(transactionProcessor);
                    // }

                    for (int i = 0; i < block.Transactions.Length; i++)
                    {
                        _balBuilder?.GeneratedBlockAccessList.IncrementBlockAccessIndex();
                        Transaction currentTx = block.Transactions[i];
                        ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);
                    }
                    _balBuilder?.GeneratedBlockAccessList.IncrementBlockAccessIndex();
                // long? gasRemaining = _balBuilder?.GasUsed();
                // if (gasRemaining is not null)
                // {
                //     _balBuilder.ValidateBlockAccessList(0, gasRemaining!.Value);
                // }

                // for (int i = 0; i < block.Transactions.Length; i++)
                // {
                //     _balBuilder?.GeneratedBlockAccessList.IncrementBlockAccessIndex();
                //     Transaction currentTx = block.Transactions[i];
                //     ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);

                //     if (gasRemaining is not null)
                //     {
                //         gasRemaining -= currentTx.SpentGas;
                //         _balBuilder.ValidateBlockAccessList((ushort)(i + 1), gasRemaining!.Value);
                //     }
                }

                return [.. receiptsTracer.TxReceipts];
            }

            private static TxReceipt[] CombineReceipts(BlockReceiptsTracer[] receiptsTracers, int len, Block block)
            {
                TxReceipt[] result = new TxReceipt[len];
                for (int i = 0; i < len; i++)
                {
                    result[i] = receiptsTracers[i].TxReceipts[0];
                    result[i].Index = i;
                }

                long cumulativeGas = 0;
                for (int i = 0; i < len; i++)
                {
                    cumulativeGas += result[i].GasUsed;
                    result[i].GasUsedTotal = cumulativeGas;
                }

                Bloom blockBloom = new();
                for (int i = 0; i < len; i++)
                {
                    result[i].CalculateBloom();
                    blockBloom.Accumulate(result[i].Bloom!);
                }
                block.Header.Bloom = blockBloom;

                return result;
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
                Console.WriteLine("[parallel] started executing transaction with bal index {index}");

                currentTx.BlockAccessIndex = index + 1;
                TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
                if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);

                Console.WriteLine($"[parallel] completed executing transaction with bal index {index + 1}");
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
