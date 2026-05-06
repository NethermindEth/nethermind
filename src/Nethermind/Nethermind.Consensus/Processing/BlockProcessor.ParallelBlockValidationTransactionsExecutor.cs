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
using Nethermind.Evm.GasPolicy;
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
                ? ProcessTransactionsParallel(block, processingOptions, receiptsTracer, token)
                : ProcessTransactionsSequential(block, processingOptions, receiptsTracer, token);
        }

        private TxReceipt[] ProcessTransactionsSequential(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            bool shouldValidate = !processingOptions.ContainsFlag(ProcessingOptions.NoValidation);
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            long totalRegularGas = 0;
            long totalStateGas = 0;

            balManager.NextTransaction();
            balManager.ValidateBlockAccessList(block, 0u);

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction currentTx = block.Transactions[i];
                IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(currentTx, spec, block.Header.GasLimit);
                if (shouldValidate)
                {
                    BlockAccessListManager.CheckPerTxInclusion(block, i, currentTx, spec, totalRegularGas, totalStateGas, in intrinsicGas);
                }

                ProcessTransaction(balManager.GetTxProcessor((uint)(i + 1)), stateProvider, block, currentTx, i, receiptsTracer, processingOptions, in intrinsicGas);
                totalRegularGas = receiptsTracer.CumulativeRegularGasUsed;
                totalStateGas = receiptsTracer.BlockStateGasUsed;

                if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)
                {
                    // Match BlockAccessListManager.IncrementalValidation's error format so
                    // both sequential and parallel paths map to the same EEST exception
                    // (TransactionException.GAS_ALLOWANCE_EXCEEDED). The sequential path
                    // previously threw ExceededGasLimit which mapped only to
                    // INVALID_GAS_USED_ABOVE_LIMIT, diverging from what fixtures expect.
                    throw new InvalidBlockException(block,
                        $"Block gas limit exceeded: cumulative gas {block.Header.GasUsed} > block gas limit {block.Header.GasLimit} after transaction index {i}.");
                }

                balManager.NextTransaction();
                balManager.SpendGas(currentTx.BlockGasUsed);
                balManager.ValidateBlockAccessList(block, (uint)(i + 1));
            }

            return [.. receiptsTracer.TxReceipts];
        }

        private TxReceipt[] ProcessTransactionsParallel(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer outerReceiptsTracer, CancellationToken token)
        {
            int len = block.Transactions.Length;
            BlockReceiptsTracer[] receiptsTracers = new BlockReceiptsTracer[len];
            TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[] gasResults = new TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>[len];

            for (int i = 0; i < len; i++)
            {
                BlockReceiptsTracer tracer = new(true);
                tracer.StartNewBlockTrace(block);
                receiptsTracers[i] = tracer;
                gasResults[i] = new TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, IntrinsicGas<EthereumGasPolicy> IntrinsicGas, InvalidBlockException? Exception)>();
            }

            // Pre-execution (the system-contract calls StoreBeaconRoot + ApplyBlockhashStateChanges)
            // runs as iteration 1 of the parallel For — alongside slot 0's loader+ApplyStateChanges
            // and the tx iterations. Its writes go through BlockAccessListBasedWorldState which is
            // a no-op for writes (the post-block state lands in stateProvider via ApplyStateChanges
            // in slot 0), and its reads block per-account on the prestate gate that slot 0's loader
            // signals — so pre-execution and tx workers all proceed as soon as their accounts are
            // loaded, without waiting for the full load.
            //
            // The validator (IncrementalValidation) needs to merge balIndex=0's BAL after this
            // iteration completes. preExecutionDoneTcs is the synchronization point: iteration 1
            // signals it on completion (or fault), and the validator awaits it before the index-0
            // merge. Counterpart of the gasResults[txIndex] dance for txs.
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            TaskCompletionSource preExecutionDoneTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task incrementalValidationTask = Task.Run(() => balManager.IncrementalValidation(block, gasResults, receiptsTracers, transactionProcessedEventHandler, preExecutionDoneTcs.Task, token), token);

            try
            {
                // Iterations: 0 = loader+ApplyStateChanges, 1 = pre-execution, 2..len+1 = tx (txIndex = i-2).
                ParallelUnbalancedWork.For(
                    0,
                    len + 2,
                    ParallelUnbalancedWork.DefaultOptions,
                    (block, processingOptions, stateProvider, balManager, receiptsTracers, gasResults, specProvider, spec, preExecutionDoneTcs, txs: block.Transactions),
                    static (i, state) =>
                    {
                        if (i == 0)
                        {
                            // Prestate loading was deferred from PrepareForProcessing to here so
                            // tx workers and the pre-execution iteration can start running while
                            // we fan out the load account-by-account. Each account's prestate
                            // gate is signaled as soon as its load completes, freeing any consumer
                            // blocked on it (see ReadOnlyAccountChanges.WaitForPrestate).
                            state.balManager.LoadPreStateToSuggestedBlockAccessList(state.block);

                            // ApplyStateChanges mutates the shared stateProvider so runs inside
                            // the parallel loop (slot 0) rather than via Task.Run. Parallel tx
                            // workers and the pre-execution iteration read from BAL-backed world
                            // states, not stateProvider, so neither races with this write.
                            BlockAccessListManager.ApplyStateChanges(state.block.BlockAccessList, state.stateProvider, state.spec, !state.block.Header.IsGenesis || !state.specProvider.GenesisStateUnavailable);
                            return state;
                        }

                        if (i == 1)
                        {
                            try
                            {
                                state.balManager.StoreBeaconRoot(state.block, state.spec);
                                state.balManager.ApplyBlockhashStateChanges(state.block.Header, state.spec);
                                state.preExecutionDoneTcs.SetResult();
                            }
                            catch (Exception ex)
                            {
                                // Forward the fault through the gate so the validator's await
                                // surfaces it instead of hanging forever.
                                state.preExecutionDoneTcs.TrySetException(ex);
                                throw;
                            }
                            return state;
                        }

                        int txIndex = i - 2;
                        Transaction tx = state.txs[txIndex];
                        // Pre-compute intrinsic gas on the worker thread; carry it through the
                        // gas-results tuple so IncrementalValidation's per-tx EIP-8037 inclusion
                        // check doesn't recalculate dynamic state-byte costs on the validator.
                        IntrinsicGas<EthereumGasPolicy> intrinsicGas = default;
                        try
                        {
                            intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, state.spec, state.block.Header.GasLimit);

                            // The using block detaches the worker's BAL into _perTxBal[balIndex]
                            // and recycles the pool slot via Dispose BEFORE we signal the gas
                            // result, so the validator finds _perTxBal[balIndex] populated when
                            // it awaits gasResults[txIndex] — even if ProcessTransaction throws.
                            // balIndex = txIndex + 1 = i - 1 (balIndex 0 is pre-execution).
                            using (TxProcessorLease lease = state.balManager.RentTxProcessor((uint)(i - 1)))
                            {
                                ProcessTransaction(
                                    lease.Adapter,
                                    state.stateProvider,
                                    state.block,
                                    tx,
                                    txIndex,
                                    state.receiptsTracers[txIndex],
                                    state.processingOptions,
                                    in intrinsicGas);
                            }
                            state.gasResults[txIndex].SetResult((tx.BlockGasUsed, state.receiptsTracers[txIndex].BlockStateGasUsed, intrinsicGas, null));
                        }
                        catch (InvalidBlockException ex)
                        {
                            // A rejected tx contributes nothing to block accumulators —
                            // the sequential path never reaches gas accounting for it because
                            // the exception bubbles up immediately. IncrementalValidation also
                            // rethrows on `ex is not null` before doing any accounting, so the
                            // tuple values here are observed only as cross-mode telemetry; we
                            // still report (0, 0) so any future consumer agrees with sequential.
                            state.gasResults[txIndex].SetResult((0, 0, intrinsicGas, ex));
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
                // Observe the validator before propagating, so its exception isn't lost as an
                // unobserved task exception. Worker TrySetCanceled and pre-execution
                // TrySetException above guarantee IncrementalValidation will unblock.
                // A fault that escapes iteration 1 before SetResult/TrySetException (e.g. between
                // StoreBeaconRoot and the catch) leaves the gate uncompleted, so cancel here as a
                // last-resort safety to avoid hanging the validator on an unsignaled gate.
                preExecutionDoneTcs.TrySetCanceled();
                try
                {
                    incrementalValidationTask.GetAwaiter().GetResult();
                }
                catch (TaskCanceledException)
                {
                    // Expected: induced by our own TrySetCanceled.
                }
                catch (Exception ex)
                {
                    // Independent secondary fault (BAL validator, gas check, etc.). Surfacing
                    // here because the original exception is what we rethrow.
                    if (_logger.IsError) _logger.Error("BAL incremental validation faulted while a parallel worker was already failing.", ex);
                }
                throw;
            }

            incrementalValidationTask.GetAwaiter().GetResult();
            try
            {
                return CombineReceipts(receiptsTracers, len, block);
            }
            finally
            {
                // Always populate the outer tracer with per-tx receipts so the parallel path's
                // tracer state matches what the sequential path would have produced. Needed for
                // BlockTraceDumper's invalid-block dump on failure, and keeps tracer state
                // consistent on success.
                HarvestPerTxReceiptsIntoOuter(receiptsTracers, outerReceiptsTracer);
            }
        }

        private static void HarvestPerTxReceiptsIntoOuter(BlockReceiptsTracer[] perTxTracers, BlockReceiptsTracer outer)
        {
            // Index-based placement preserves tx order despite parallel out-of-order completion;
            // gaps for txs the worker threw on (no MarkAs* fired) stay null so the dump shows
            // exactly which tx caused the rejection. Recompute GasUsedTotal across the harvested
            // sequence: each per-tx tracer's _cumulativeReceiptGas only tracks that single tx
            // (resets to 0 per tracer), so the dump would otherwise show GasUsedTotal = GasUsed.
            long cumulativeGas = 0;
            for (int i = 0; i < perTxTracers.Length; i++)
            {
                ReadOnlySpan<TxReceipt> receipts = perTxTracers[i].TxReceipts;
                if (receipts.IsEmpty) continue;
                TxReceipt receipt = receipts[0];
                cumulativeGas += receipt.GasUsed;
                receipt.GasUsedTotal = cumulativeGas;
                outer.SetReceipt(i, receipt);
            }
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

        private static void ProcessTransaction(
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider,
            Block block,
            Transaction currentTx,
            int index,
            BlockReceiptsTracer receiptsTracer,
            ProcessingOptions processingOptions,
            in IntrinsicGas<EthereumGasPolicy> intrinsicGas)
        {
            TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider, in intrinsicGas);
            if (!result) BlockValidationTransactionsExecutor.ThrowInvalidTransactionException(result, block.Header, currentTx, index);
        }
    }
}
