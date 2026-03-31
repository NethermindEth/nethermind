// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

public partial class ParallelBlockProcessor
{
    public class ParallelBlockValidationTransactionsExecutor(
        IWorldState stateProvider,
        ITransactionProcessorAdapter transactionProcessor,
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ISpecProvider specProvider,
        IBlockhashProvider blockHashProvider,
        ILogManager logManager,
        IBlocksConfig blocksConfig,
        BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
        : BlockValidationTransactionsExecutor(transactionProcessor, stateProvider, transactionProcessedEventHandler)
    {
        private BlockExecutionContext _blockExecutionContext;
        private TxReceipt[] _txReceipts;

        public override void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
            _blockExecutionContext = blockExecutionContext;
        }

        public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            Metrics.ResetBlockStats();

            _txReceipts = blocksConfig.ParallelExecution && !block.IsGenesis ?
                ProcessTransactionsParallel(block, processingOptions, token) :
                ProcessTransactionsSequential(block, processingOptions, receiptsTracer, token);
            
            return _txReceipts;
        }

        private TxReceipt[] ProcessTransactionsSequential(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            // VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
            // ParallelWorldState parallelWorldState = new(stateProvider, specProvider, blocksConfig, -1, block, new(), GeneratedBlockAccessList, new(logManager), processingOptions.ContainsFlag(ProcessingOptions.ProducingBlock));
            // TransactionProcessor<EthereumGasPolicy> transactionProcessor = new(blobBaseFeeCalculator, specProvider, parallelWorldState, virtualMachine, codeInfoRepository, logManager);
            // ExecuteTransactionProcessorAdapter transactionProcessorAdapter = CreateTransactionProcessor(block, -1, _intermediateBlockAccessLists[bal], );
            // transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);


            long? gasRemaining = _gasUsed;
            if (gasRemaining is not null)
            {
                ValidateBlockAccessList(block, 0, gasRemaining.Value);
            }

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                GeneratedBlockAccessList.IncrementBlockAccessIndex();
                Transaction currentTx = block.Transactions[i];
                ProcessTransaction(_transactionProcessorAdapters[i + 1], block, currentTx, i, receiptsTracer, processingOptions);

                if (gasRemaining is not null)
                {
                    gasRemaining -= currentTx.BlockGasUsed;
                    ValidateBlockAccessList(block, (ushort)(i + 1), gasRemaining!.Value);
                }
            }
            GeneratedBlockAccessList.IncrementBlockAccessIndex();

            return [.. receiptsTracer.TxReceipts];
        }

        private TxReceipt[] ProcessTransactionsParallel(Block block, ProcessingOptions processingOptions, CancellationToken token)
        {
            int len = block.Transactions.Length;
            TransientStorageProvider[] transientStorageProviders = new TransientStorageProvider[len + 2];
            BlockReceiptsTracer[] receiptsTracers = new BlockReceiptsTracer[len];
            TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[] gasResults = new TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[len];

            for (int i = 0; i < len + 2; i++)
            {
                transientStorageProviders[i] = new(logManager);
            }

            for (int i = 0; i < len; i++)
            {
                BlockReceiptsTracer tracer = new();
                tracer.StartNewBlockTrace(block);
                receiptsTracers[i] = tracer;
                gasResults[i] = new TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>();
            }

            Task incrementalValidationTask = Task.Run(() => IncrementalValidation(block, gasResults, receiptsTracers), token);

            ParallelUnbalancedWork.For(
                0,
                len + 1,
                ParallelUnbalancedWork.DefaultOptions,
                (block, processingOptions, _stateProvider, transactionProcessors: _transactionProcessorAdapters, receiptsTracers, gasResults, specProvider, txs: block.Transactions),
                static (i, state) =>
                {
                    if (i == 0)
                    {
                        // (state.stateProvider as IBlockAccessListBuilder)?.ApplyStateChanges(state.specProvider.GetSpec(state.block.Header), !state.block.Header.IsGenesis || !state.specProvider.GenesisStateUnavailable);
                        ApplyStateChanges(state.block.BlockAccessList, state.stateProvider, state.specProvider.GetSpec(state.block.Header), !state.block.Header.IsGenesis || !state.specProvider.GenesisStateUnavailable);
                        return state;
                    }

                    int txIndex = i - 1;
                    try
                    {
                        Transaction tx = state.txs[txIndex];
                        ProcessTransactionParallel(
                            state.transactionProcessors[i],
                            state.stateProvider,
                            state.block,
                            tx,
                            txIndex,
                            state.receiptsTracers[txIndex],
                            state.processingOptions);
                        state.gasResults[txIndex].SetResult((tx.BlockGasUsed, null));
                    }
                    catch (Exception ex)
                    {
                        state.gasResults[txIndex].SetResult((null, ex));
                    }

                    return state;
                });

            incrementalValidationTask.GetAwaiter().GetResult();
            return CombineReceipts(receiptsTracers, len, block);
        }

        private static TxReceipt[] CombineReceipts(BlockReceiptsTracer[] receiptsTracers, int len, Block block)
        {
            TxReceipt[] result = new TxReceipt[len];
            long cumulativeGas = 0;
            Bloom blockBloom = new();
            for (int i = 0; i < len; i++)
            {
                result[i] = receiptsTracers[i].TxReceipts[0];
                result[i].Index = i;
                cumulativeGas += result[i].GasUsed;
                result[i].GasUsedTotal = cumulativeGas;
                result[i].CalculateBloom();
                blockBloom.Accumulate(result[i].Bloom!);
            }

            block.Header.Bloom = blockBloom;

            return result;
        }

    }
}