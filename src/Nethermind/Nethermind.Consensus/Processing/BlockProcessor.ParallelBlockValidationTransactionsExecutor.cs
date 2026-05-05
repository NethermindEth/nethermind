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
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    public class ParallelBlockValidationTransactionsExecutor(
        IBlockProcessor.IBlockTransactionsExecutor inner,
        IWorldState stateProvider,
        ISpecProvider specProvider,
        IBlockAccessListManager balManager,
        ILogManager logManager,
        BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
        : IBlockProcessor.IBlockTransactionsExecutor
    {
        private readonly ILogger _logger = logManager.GetClassLogger<ParallelBlockValidationTransactionsExecutor>();

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
            balManager.SetBlockExecutionContext(blockExecutionContext);
            inner.SetBlockExecutionContext(blockExecutionContext);
        }

        public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            if (!balManager.Enabled)
            {
                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);
            }

            Metrics.ResetBlockStats();

            return !block.IsGenesis && balManager.ParallelExecutionEnabled
                ? ProcessTransactionsParallel(block, processingOptions, token)
                : ProcessTransactionsSequential(block, processingOptions, receiptsTracer, token);
        }

        private TxReceipt[] ProcessTransactionsSequential(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            balManager.NextTransaction();
            balManager.ValidateBlockAccessList(block, 0);

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction currentTx = block.Transactions[i];
                ProcessTransaction(balManager.GetTxProcessor(i + 1), stateProvider, block, currentTx, i, receiptsTracer, processingOptions);

                balManager.NextTransaction();
                balManager.SpendGas(currentTx.BlockGasUsed);
                balManager.ValidateBlockAccessList(block, (ushort)(i + 1));
            }

            return [.. receiptsTracer.TxReceipts];
        }

        private TxReceipt[] ProcessTransactionsParallel(Block block, ProcessingOptions processingOptions, CancellationToken token)
        {
            int len = block.Transactions.Length;
            BlockReceiptsTracer[] receiptsTracers = new BlockReceiptsTracer[len];
            TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, InvalidBlockException? Exception)>[] gasResults = new TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, InvalidBlockException? Exception)>[len];

            for (int i = 0; i < len; i++)
            {
                BlockReceiptsTracer tracer = new(true);
                tracer.StartNewBlockTrace(block);
                receiptsTracers[i] = tracer;
                gasResults[i] = new TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, InvalidBlockException? Exception)>();
            }

            Task incrementalValidationTask = Task.Run(() => balManager.IncrementalValidation(block, gasResults, receiptsTracers, transactionProcessedEventHandler, token), token);

            try
            {
                // ParallelUnbalancedWork handles uneven tx execution times better than Parallel.For
                ParallelUnbalancedWork.For(
                    0,
                    len + 1,
                    ParallelUnbalancedWork.DefaultOptions,
                    (block, processingOptions, stateProvider, balManager, receiptsTracers, gasResults, specProvider, txs: block.Transactions),
                    static (i, state) =>
                    {
                        if (i == 0)
                        {
                            // ApplyStateChanges mutates the shared stateProvider so runs inside
                            // the parallel loop (slot 0) rather than via Task.Run. Parallel tx
                            // workers read from BAL-backed world states, not stateProvider.
                            BlockAccessListManager.ApplyStateChanges(state.block.BlockAccessList, state.stateProvider, state.specProvider.GetSpec(state.block.Header), !state.block.Header.IsGenesis || !state.specProvider.GenesisStateUnavailable);
                            return state;
                        }

                        int txIndex = i - 1;
                        Transaction tx = state.txs[txIndex];
                        try
                        {
                            // The using block detaches the worker's BAL into _perTxBal[i] and
                            // recycles the pool slot via Dispose BEFORE we signal the gas result,
                            // so the validator finds _perTxBal[i] populated when it awaits
                            // gasResults[i-1] — even if ProcessTransaction throws.
                            using (TxProcessorLease lease = state.balManager.RentTxProcessor(i))
                            {
                                ProcessTransaction(
                                    lease.Adapter,
                                    state.stateProvider,
                                    state.block,
                                    tx,
                                    txIndex,
                                    state.receiptsTracers[txIndex],
                                    state.processingOptions);
                            }
                            state.gasResults[txIndex].SetResult((tx.BlockGasUsed, state.receiptsTracers[txIndex].BlockStateGasUsed, null));
                        }
                        catch (InvalidBlockException ex)
                        {
                            state.gasResults[txIndex].SetResult((tx.GasLimit, 0, ex));
                        }
                        catch
                        {
                            // Ensure IncrementalValidation is not permanently blocked on gasResults[j]
                            // if an unexpected exception escapes the worker (e.g. NRE, OCE).
                            // SetCanceled unblocks the inner GetAwaiter().GetResult() loop.
                            state.gasResults[txIndex].TrySetCanceled();
                            throw;
                        }

                        return state;
                    });
            }
            catch
            {
                // Observe the background task before propagating, so its exception isn't lost
                // as an unobserved task exception. The worker's TrySetCanceled above guarantees
                // IncrementalValidation will unblock and complete.
                try
                {
                    incrementalValidationTask.GetAwaiter().GetResult();
                }
                catch (TaskCanceledException)
                {
                    // Expected: induced by our own TrySetCanceled in the worker catch.
                }
                catch (Exception ex)
                {
                    // Independent secondary fault (BAL validator, gas check, etc.). Surfacing
                    // here because the original exception is what we rethrow — this branch is
                    // the only place this fault is observable.
                    if (_logger.IsError) _logger.Error("BAL incremental validation faulted while a parallel worker was already failing.", ex);
                }
                throw;
            }

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
            if (!result) BlockValidationTransactionsExecutor.ThrowInvalidTransactionException(result, block.Header, currentTx, index);
        }
    }
}
