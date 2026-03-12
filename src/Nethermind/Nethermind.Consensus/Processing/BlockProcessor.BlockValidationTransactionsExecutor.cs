// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
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
            IBlockhashProvider blockHashProvider,
            ICodeInfoRepository codeInfoRepository,
            ILogManager logManager,
            BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
            : IBlockProcessor.IBlockTransactionsExecutor
        {
            private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;
            private readonly ITransactionProcessorAdapter _transactionProcessor = transactionProcessor;
            private BlockExecutionContext _blockExecutionContext;
            private ILogger _logger = logManager.GetClassLogger();

            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            {
                _transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
                _blockExecutionContext = blockExecutionContext;
            }

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
            {
                Metrics.ResetBlockStats();

                int len = block.Transactions.Length;
                if (_balBuilder is not null && _balBuilder.ParallelExecutionEnabled)
                {
                    _logger.Info($"[parallel] beginning parallel execution for block {block.Number}");

                    ITransactionProcessorAdapter[] transactionProcessors = new ITransactionProcessorAdapter[len];
                    BlockReceiptsTracer[] receiptsTracers = new BlockReceiptsTracer[len]; // n.b. other tracer not set
                    TaskCompletionSource<(long? BlockGasUsed, long? SpentGas, Exception? Exception)>[] gasResults = new TaskCompletionSource<(long? BlockGasUsed, long? SpentGas, Exception? Exception)>[len];
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
                        gasResults[i] = new TaskCompletionSource<(long? BlockGasUsed, long? SpentGas, Exception? Exception)>();
                    }

                    const int GasValidationChunkSize = 8;
                    Task validatorTask = Task.Run(() =>
                    {
                        long gasRemaining = _balBuilder.GasUsed();
                        _logger.Info($"[parallel] running gas validation, total gas: {gasRemaining}");

                        _balBuilder.ValidateBlockAccessList(block.Header, 0, gasRemaining);

                        long totalGas = 0;
                        long totalGasTx = 0;
                        for (int chunkStart = 0; chunkStart < len; chunkStart += GasValidationChunkSize)
                        {
                            int chunkEnd = Math.Min(chunkStart + GasValidationChunkSize, len);
                            for (int j = chunkStart; j < chunkEnd; j++)
                            {
                                (long? blockGasUsed, long? txGasSpent, Exception? ex) = gasResults[j].Task.GetAwaiter().GetResult();
                                if (ex is not null)
                                    ExceptionDispatchInfo.Capture(ex).Throw();

                                totalGas += blockGasUsed.Value;
                                totalGasTx += txGasSpent.Value;
                                gasRemaining -= txGasSpent.Value;

                                bool validateStorageReads = j == chunkEnd - 1;
                                _balBuilder.MergeIntermediateBalsUpTo((ushort)(j + 1));
                                _logger.Info($"[parallel] validating block access list at index {j + 1} (storage read check: {validateStorageReads}), gas spent: {txGasSpent!.Value}, block gas used: {blockGasUsed.Value}, remaining: {gasRemaining}, total: {totalGas}");
                                _balBuilder.ValidateBlockAccessList(block.Header, (ushort)(j + 1), gasRemaining, validateStorageReads);
                            }

                            if (totalGas > block.Header.GasLimit)
                            {
                                throw new InvalidBlockException(block, $"Block gas limit exceeded: cumulative gas {totalGas} > block gas limit {block.Header.GasLimit} after transaction index {chunkEnd - 1}.");
                            }
                        }
                        _blockExecutionContext.Header.GasUsed = totalGasTx;
                    }, token);

                    ParallelUnbalancedWork.For(
                        0,
                        len + 1,
                        ParallelUnbalancedWork.DefaultOptions,
                        (block, processingOptions, stateProvider, transactionProcessors, receiptsTracers, gasResults, specProvider, txs: block.Transactions, logger: _logger),
                        static (i, state) =>
                        {
                            if (i == 0)
                            {
                                (state.stateProvider as IBlockAccessListBuilder)?.ApplyStateChanges(state.specProvider.GetSpec(state.block.Header), !state.block.Header.IsGenesis || !state.specProvider.GenesisStateUnavailable);
                                return state;
                            }

                            int txIndex = i - 1;
                            try
                            {
                                Transaction tx = state.txs[txIndex];
                                ProcessTransactionParallel(
                                    state.transactionProcessors[txIndex],
                                    state.stateProvider,
                                    state.specProvider,
                                    state.block,
                                    tx,
                                    txIndex,
                                    state.receiptsTracers[txIndex],
                                    state.processingOptions,
                                    state.logger);
                                long spentGas = tx.SpentGas;
                                long blockGasUsed = tx.BlockGasUsed;
                                // blockGasUsed = blockGasUsed == tx.GasLimit ? spentGas : blockGasUsed;
                                state.gasResults[txIndex].SetResult((blockGasUsed, spentGas, null));
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"parallel exec error: {ex}");
                                state.gasResults[txIndex].SetResult((null, null, ex));
                            }

                            return state;
                        });

                    validatorTask.GetAwaiter().GetResult();
                    return CombineReceipts(receiptsTracers, len, block);
                }
                else
                {
                    long? gasRemaining = _balBuilder?.GasUsed();
                    if (gasRemaining is not null)
                    {
                        _balBuilder!.ValidateBlockAccessList(block.Header, 0, gasRemaining.Value);
                    }

                    for (int i = 0; i < block.Transactions.Length; i++)
                    {
                        _balBuilder?.GeneratedBlockAccessList.IncrementBlockAccessIndex();
                        Transaction currentTx = block.Transactions[i];
                        ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);

                        if (gasRemaining is not null)
                        {
                            gasRemaining -= currentTx.SpentGas;
                            _balBuilder!.ValidateBlockAccessList(block.Header, (ushort)(i + 1), gasRemaining!.Value);
                        }
                    }
                    _balBuilder?.GeneratedBlockAccessList.IncrementBlockAccessIndex();
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
                currentTx.BlockAccessIndex = index + 1;
                TransactionResult result = _transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
                if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
                transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
            }

            private static void ProcessTransactionParallel(
                ITransactionProcessorAdapter transactionProcessor,
                IWorldState stateProvider,
                ISpecProvider specProvider,
                Block block,
                Transaction currentTx,
                int index,
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions,
                ILogger logger)
            {
                logger.Info($"[parallel] started executing transaction with bal index {index + 1}");

                currentTx.BlockAccessIndex = index + 1;
                TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
                if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);

                logger.Info($"[parallel] completed executing transaction with bal index {index + 1}");
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
