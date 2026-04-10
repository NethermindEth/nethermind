// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public class ParallelBlockValidationTransactionsExecutor(
        IWorldState stateProvider,
        ITransactionProcessorAdapter transactionProcessor,
        ISpecProvider specProvider,
        IBlockAccessListManager balManager,
        BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
        : BlockValidationTransactionsExecutor(transactionProcessor, stateProvider, transactionProcessedEventHandler)
    {
        private TxReceipt[] _txReceipts;

        public override void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
            balManager.SetBlockExecutionContext(blockExecutionContext);
            base.SetBlockExecutionContext(blockExecutionContext);
        }

        public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            if (!balManager.Enabled)
            {
                return base.ProcessTransactions(block, processingOptions, receiptsTracer, token);
            }

            Metrics.ResetBlockStats();

            _txReceipts = !block.IsGenesis && balManager.ParallelExecutionEnabled ?
                ProcessTransactionsParallel(block, processingOptions, token) :
                ProcessTransactionsSequential(block, processingOptions, receiptsTracer, token);

            return _txReceipts;
        }

        private TxReceipt[] ProcessTransactionsSequential(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            balManager.ValidateBlockAccessList(block, 0);

            balManager.NextTransaction();

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction currentTx = block.Transactions[i];
                ProcessTransaction(balManager.GetTxProcessor(i + 1), _stateProvider, block, currentTx, i, receiptsTracer, processingOptions);

                balManager.SpendGas(currentTx.BlockGasUsed);
                balManager.ValidateBlockAccessList(block, (ushort)(i + 1));
                balManager.NextTransaction();
            }

            return [.. receiptsTracer.TxReceipts];
        }

        private TxReceipt[] ProcessTransactionsParallel(Block block, ProcessingOptions processingOptions, CancellationToken token)
        {
            int len = block.Transactions.Length;
            BlockReceiptsTracer[] receiptsTracers = new BlockReceiptsTracer[len];
            TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[] gasResults = new TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[len];

            for (int i = 0; i < len; i++)
            {
                BlockReceiptsTracer tracer = new();
                tracer.StartNewBlockTrace(block);
                receiptsTracers[i] = tracer;
                gasResults[i] = new TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>();
            }

            Task incrementalValidationTask = Task.Run(() => balManager.IncrementalValidation(block, gasResults, receiptsTracers, _transactionProcessedEventHandler), token);

            // todo: change to Parallel.For?
            ParallelUnbalancedWork.For(
                0,
                len + 1,
                ParallelUnbalancedWork.DefaultOptions,
                (block, processingOptions, stateProvider: _stateProvider, balManager, receiptsTracers, gasResults, specProvider, txs: block.Transactions),
                static (i, state) =>
                {
                    if (i == 0)
                    {
                        // todo: this is a bit weird, but there was an error about accessing WorldState when executing using Task.Run
                        // need to investigate more
                        BlockAccessListManager.ApplyStateChanges(state.block.BlockAccessList, state.stateProvider, state.specProvider.GetSpec(state.block.Header), !state.block.Header.IsGenesis || !state.specProvider.GenesisStateUnavailable);
                        return state;
                    }

                    int txIndex = i - 1;
                    try
                    {
                        Transaction tx = state.txs[txIndex];
                        ProcessTransaction(
                            state.balManager.GetTxProcessor(i),
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

        private static void ProcessTransaction(
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider,
            Block block,
            Transaction currentTx,
            int index,
            BlockReceiptsTracer receiptsTracer,
            ProcessingOptions processingOptions)
        {
            TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
            if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
        }
    }
}
