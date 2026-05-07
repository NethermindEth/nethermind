// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
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
        private readonly IncrementalValidationWorkItem _incrementalValidationWorkItem = new();
        private BlockReceiptsTracer[] _receiptsTracerPool = [];
        private GasValidationResultSlot[] _gasResultPool = [];

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
            balManager.ValidateBlockAccessList(block, 0);

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
            bool isBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
            IBlockTracer parallelSafeTracer = GetParallelSafeTracer(outerReceiptsTracer.OtherTracer);
            EnsureParallelBuffers(len);
            BlockReceiptsTracer[] receiptsTracers = _receiptsTracerPool;
            GasValidationResultSlot[] gasResults = _gasResultPool;
            for (int i = 0; i < len; i++)
            {
                receiptsTracers[i].ResetForParallelTx(block, parallelSafeTracer);
                gasResults[i].Reset();
            }

            IncrementalValidationWorkItem incrementalValidation = _incrementalValidationWorkItem;
            incrementalValidation.Schedule(balManager, block, gasResults, receiptsTracers, transactionProcessedEventHandler, token);

            try
            {
                try
                {
                    // ParallelUnbalancedWork handles uneven tx execution times better than Parallel.For
                    ParallelUnbalancedWork.For(
                        0,
                        len + 1,
                        ParallelUnbalancedWork.DefaultOptions,
                        (block, processingOptions, stateProvider, balManager, receiptsTracers, gasResults, specProvider,
                            txs: block.Transactions, isBlockProcessingThread),
                        static (i, state) =>
                        {
                            bool previousIsBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
                            ProcessingThread.IsBlockProcessingThread = state.isBlockProcessingThread;
                            try
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
                                IntrinsicGas<EthereumGasPolicy> intrinsicGas = default;
                                try
                                {
                                    intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, state.specProvider.GetSpec(state.block.Header), state.block.Header.GasLimit);

                                    // The using block detaches the worker's BAL into _perTxBal[i] and
                                    // recycles the pool slot via Dispose BEFORE we signal the gas result,
                                    // so the validator finds _perTxBal[i] populated when it awaits
                                    // gasResults[i-1] — even if ProcessTransaction throws.
                                    using (TxProcessorLease lease = state.balManager.RentTxProcessor((uint)i))
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
                                    state.gasResults[txIndex].TrySetResult(new GasValidationResult(tx.BlockGasUsed, state.receiptsTracers[txIndex].BlockStateGasUsed, in intrinsicGas, null));
                                }
                                catch (InvalidBlockException ex)
                                {
                                    // A rejected tx contributes nothing to block accumulators —
                                    // the sequential path never reaches gas accounting for it because
                                    // the exception bubbles up immediately. IncrementalValidation also
                                    // rethrows on `ex is not null` before doing any accounting, so the
                                    // tuple values here are observed only as cross-mode telemetry; we
                                    // still report (0, 0) so any future consumer agrees with sequential.
                                    state.gasResults[txIndex].TrySetResult(new GasValidationResult(0, 0, in intrinsicGas, ex));
                                }
                                catch
                                {
                                    // Ensure IncrementalValidation is not permanently blocked on gasResults[j]
                                    // if an unexpected exception escapes the worker (e.g. NRE, OCE).
                                    // TrySetCanceled unblocks the validator's slot wait.
                                    state.gasResults[txIndex].TrySetCanceled();
                                    throw;
                                }

                                return state;
                            }
                            finally
                            {
                                ProcessingThread.IsBlockProcessingThread = previousIsBlockProcessingThread;
                            }
                        });
                }
                catch
                {
                    // Observe the background task before propagating, so its exception isn't lost
                    // as an unobserved task exception. The worker's TrySetCanceled above guarantees
                    // IncrementalValidation will unblock and complete.
                    try
                    {
                        incrementalValidation.GetResult();
                    }
                    catch (OperationCanceledException ex) when (ex is TaskCanceledException || token.IsCancellationRequested)
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

                incrementalValidation.GetResult();
                return CombineReceipts(receiptsTracers, len, block);
            }
            finally
            {
                // Always populate the outer tracer with per-tx receipts so the parallel path's
                // tracer state matches what the sequential path would have produced. Needed for
                // BlockTraceDumper's invalid-block dump on failure, and keeps tracer state
                // consistent on success.
                HarvestPerTxReceiptsIntoOuter(receiptsTracers, len, outerReceiptsTracer);
            }
        }

        private void EnsureParallelBuffers(int length)
        {
            int currentLength = _receiptsTracerPool.Length;
            if (currentLength >= length)
            {
                return;
            }

            int newLength = Math.Max(length, currentLength == 0 ? 4 : currentLength * 2);
            Array.Resize(ref _receiptsTracerPool, newLength);
            Array.Resize(ref _gasResultPool, newLength);
            for (int i = currentLength; i < newLength; i++)
            {
                _receiptsTracerPool[i] = new BlockReceiptsTracer(true);
                _gasResultPool[i] = new GasValidationResultSlot();
            }
        }

        private static IBlockTracer GetParallelSafeTracer(IBlockTracer tracer) =>
            tracer switch
            {
                IParallelSafeBlockTracer => tracer,
                CompositeBlockTracer compositeBlockTracer => compositeBlockTracer.GetParallelSafeTracer(),
                _ => NullBlockTracer.Instance
            };

        private static void HarvestPerTxReceiptsIntoOuter(BlockReceiptsTracer[] perTxTracers, int length, BlockReceiptsTracer outer)
        {
            // Index-based placement preserves tx order despite parallel out-of-order completion;
            // gaps for txs the worker threw on (no MarkAs* fired) stay null so the dump shows
            // exactly which tx caused the rejection. Recompute GasUsedTotal across the harvested
            // sequence: each per-tx tracer's _cumulativeReceiptGas only tracks that single tx
            // (resets to 0 per tracer), so the dump would otherwise show GasUsedTotal = GasUsed.
            long cumulativeGas = 0;
            for (int i = 0; i < length; i++)
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
            ProcessingOptions processingOptions,
            in IntrinsicGas<EthereumGasPolicy> intrinsicGas)
        {
            TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider, in intrinsicGas);
            if (!result) BlockValidationTransactionsExecutor.ThrowInvalidTransactionException(result, block.Header, currentTx, index);
        }

        private sealed class IncrementalValidationWorkItem : IThreadPoolWorkItem
        {
            private readonly ManualResetEventSlim _completed = new(false);
            private IBlockAccessListManager? _balManager;
            private Block? _block;
            private GasValidationResultSlot[]? _gasResults;
            private BlockReceiptsTracer[]? _receiptsTracers;
            private BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? _transactionProcessedEventHandler;
            private CancellationToken _token;
            private Exception? _exception;

            public void Schedule(
                IBlockAccessListManager balManager,
                Block block,
                GasValidationResultSlot[] gasResults,
                BlockReceiptsTracer[] receiptsTracers,
                BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler,
                CancellationToken token)
            {
                _completed.Reset();
                _exception = null;

                if (token.IsCancellationRequested)
                {
                    _exception = new TaskCanceledException();
                    _completed.Set();
                    return;
                }

                _balManager = balManager;
                _block = block;
                _gasResults = gasResults;
                _receiptsTracers = receiptsTracers;
                _transactionProcessedEventHandler = transactionProcessedEventHandler;
                _token = token;
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }

            public void GetResult()
            {
                _completed.Wait();
                if (_exception is not null)
                {
                    ExceptionDispatchInfo.Capture(_exception).Throw();
                }
            }

            void IThreadPoolWorkItem.Execute()
            {
                IBlockAccessListManager balManager = _balManager!;
                Block block = _block!;
                GasValidationResultSlot[] gasResults = _gasResults!;
                BlockReceiptsTracer[] receiptsTracers = _receiptsTracers!;
                BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = _transactionProcessedEventHandler;
                CancellationToken token = _token;

                _balManager = null;
                _block = null;
                _gasResults = null;
                _receiptsTracers = null;
                _transactionProcessedEventHandler = null;
                _token = default;

                try
                {
                    balManager.IncrementalValidation(block, gasResults, receiptsTracers, transactionProcessedEventHandler, token);
                }
                catch (Exception ex)
                {
                    _exception = ex;
                }
                finally
                {
                    _completed.Set();
                }
            }
        }
    }
}
