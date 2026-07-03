// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Eip2930;
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
        private int[] _txExecutionOrder = [];
        private TxExecutionSortKey[] _txExecutionSortKeys = [];

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
            inner.SetupTxTimingMetrics(block);

            TxReceipt[] receipts = !block.IsGenesis && balManager.ParallelExecutionEnabled
                ? ProcessTransactionsParallel(block, processingOptions, receiptsTracer, token)
                : ProcessTransactionsSequential(block, processingOptions, receiptsTracer, token);

            // Seed empty/system-only blocks with the base fee, then publish gauges once - after workers
            // join - from the final aggregates so a stale worker view cannot overwrite them.
            Metrics.SeedBlockGasPriceIfEmpty(block.Header.BaseFeePerGas);
            Metrics.PublishBlockGasPriceGauges();

            return receipts;
        }

        private TxReceipt[] ProcessTransactionsSequential(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            bool shouldValidate = !processingOptions.ContainsFlag(ProcessingOptions.NoValidation);
            // Block-building has no suggested BAL to compare against — we are producing it
            // here. ValidateBlockAccessList would early-return on `BlockAccessList is null`
            // anyway, but skipping the call avoids the NextTransaction → Validate dance and
            // makes the building intent explicit on this hot path.
            bool shouldValidateBal = shouldValidate && !processingOptions.ContainsFlag(ProcessingOptions.ProducingBlock);
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            ulong totalRegularGas = 0;
            ulong totalStateGas = 0;

            balManager.NextTransaction();
            if (shouldValidateBal) balManager.ValidateBlockAccessList(block, 0);

            for (uint i = 0; i < block.Transactions.Length; i++)
            {
                Transaction currentTx = block.Transactions[i];
                IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(currentTx, spec, block.Header.GasLimit);
                if (shouldValidate)
                {
                    BlockAccessListManager.CheckPerTxInclusion(block, (int)i, currentTx, spec, totalRegularGas, totalStateGas, in intrinsicGas);
                }

                ProcessTransaction(balManager.GetTxProcessor(i + 1), stateProvider, block, currentTx, (int)i, receiptsTracer, processingOptions, inner, in intrinsicGas);
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
                if (shouldValidateBal) balManager.ValidateBlockAccessList(block, i + 1);
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
            BuildTxExecutionOrder(block.Transactions, _txExecutionOrder, _txExecutionSortKeys, GetCanonicalExecutionLead(len));

            try
            {
                try
                {
                    // Iterations: 0 = ApplyStateChanges, 1..len = tx (scheduled order =
                    // _txExecutionOrder[i-1]; balIndex = scheduledTxIndex+1). Pre-execution
                    // (StoreBeaconRoot + ApplyBlockhashStateChanges) ran sequentially in
                    // BlockProcessor.ProcessBlock before this method was called.
                    ParallelUnbalancedWork.For(
                        0,
                        len + 1,
                        ParallelUnbalancedWork.DefaultOptions,
                        (block, processingOptions, stateProvider, balManager, receiptsTracers, gasResults, specProvider,
                            txs: block.Transactions, txExecutionOrder: _txExecutionOrder, isBlockProcessingThread, inner),
                        static (i, state) =>
                        {
                            // Propagate the parent thread's IsBlockProcessingThread flag onto the
                            // worker so processing-stats heuristics (e.g. allocation-thread filters)
                            // continue to attribute work correctly across the parallel boundary.
                            bool previousIsBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
                            ProcessingThread.IsBlockProcessingThread = state.isBlockProcessingThread;
                            try
                            {
                                if (i == 0)
                                {
                                    state.balManager.WaitForBalWarmup();
                                    BlockAccessListManager.ApplyStateChanges(state.block.BlockAccessList, state.stateProvider, state.specProvider.GetSpec(state.block.Header), !state.block.Header.IsGenesis || !state.specProvider.GenesisStateUnavailable);
                                    return state;
                                }

                                int txIndex = state.txExecutionOrder[i - 1];
                                Transaction tx = state.txs[txIndex];
                                // Pre-compute intrinsic gas on the worker thread; carry it through the
                                // gas-results tuple so IncrementalValidation's per-tx EIP-8037 inclusion
                                // check doesn't recalculate dynamic state-byte costs on the validator.
                                IntrinsicGas<EthereumGasPolicy> intrinsicGas = default;
                                try
                                {
                                    intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, state.specProvider.GetSpec(state.block.Header), state.block.Header.GasLimit);

                                    // The using block detaches the worker's BAL into _perTxBal[txIndex + 1] and
                                    // recycles the pool slot via Dispose BEFORE we signal the gas result,
                                    // so the validator finds the canonical BAL slot populated when it awaits
                                    // gasResults[txIndex] — even if ProcessTransaction throws.
                                    using (TxProcessorLease lease = state.balManager.RentTxProcessor((uint)(txIndex + 1)))
                                    {
                                        ProcessTransaction(
                                            lease.Adapter,
                                            state.stateProvider,
                                            state.block,
                                            tx,
                                            txIndex,
                                            state.receiptsTracers[txIndex],
                                            state.processingOptions,
                                            state.inner,
                                            in intrinsicGas);
                                    }
                                    state.gasResults[txIndex].TrySetResult(new GasValidationResult(tx.BlockGasUsed, state.receiptsTracers[txIndex].BlockStateGasUsed, intrinsicGas, null));
                                }
                                catch (InvalidBlockException ex)
                                {
                                    // A rejected tx contributes nothing to block accumulators —
                                    // the sequential path never reaches gas accounting for it because
                                    // the exception bubbles up immediately. IncrementalValidation also
                                    // rethrows on `ex is not null` before doing any accounting, so the
                                    // tuple values here are observed only as cross-mode telemetry; we
                                    // still report (0, 0) so any future consumer agrees with sequential.
                                    state.gasResults[txIndex].TrySetResult(new GasValidationResult(0, 0, intrinsicGas, ex));
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
                    CancelIncompleteGasResults(gasResults, len);

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
                return CombineReceipts(receiptsTracers, len);
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

            // Resize (rather than allocate fresh) preserves the BlockReceiptsTracer and
            // GasValidationResultSlot instances already pooled in slots [0, currentLength);
            // freshly allocated arrays would force re-instantiation of every slot every block.
            int newLength = Math.Max(length, currentLength == 0 ? 4 : currentLength * 2);
            Array.Resize(ref _receiptsTracerPool, newLength);
            Array.Resize(ref _gasResultPool, newLength);
            Array.Resize(ref _txExecutionOrder, newLength);
            Array.Resize(ref _txExecutionSortKeys, newLength);
            for (int i = currentLength; i < newLength; i++)
            {
                _receiptsTracerPool[i] = new BlockReceiptsTracer(true);
                _gasResultPool[i] = new GasValidationResultSlot();
            }
        }

        /// <summary>Canonical tx-execution lead: the prefix of the schedule that always runs in
        /// natural block order. Chosen so single- and small-tx blocks don't pay the sort cost;
        /// larger blocks reorder the tail to surface the heaviest gas-limit txs first, which
        /// reduces tail-latency stragglers in <see cref="ParallelUnbalancedWork.For"/>.</summary>
        internal static int GetCanonicalExecutionLead(int txCount)
        {
            int lead = Math.Max(8, Nethermind.Core.Cpu.RuntimeInformation.ProcessorCount * 2);
            return Math.Min(txCount, lead);
        }

        internal static void BuildTxExecutionOrder(Transaction[] txs, int[] txExecutionOrder, int canonicalLead)
        {
            TxExecutionSortKey[] sortKeys = new TxExecutionSortKey[txs.Length];
            BuildTxExecutionOrder(txs, txExecutionOrder, sortKeys, canonicalLead);
        }

        private static void BuildTxExecutionOrder(
            Transaction[] txs,
            int[] txExecutionOrder,
            TxExecutionSortKey[] sortKeys,
            int canonicalLead)
        {
            int len = txs.Length;
            for (int i = 0; i < len; i++)
            {
                txExecutionOrder[i] = i;
            }

            int lead = Math.Clamp(canonicalLead, 0, len);
            int sortCount = len - lead;
            if (sortCount <= 1)
            {
                return;
            }

            for (int i = lead; i < len; i++)
            {
                sortKeys[i] = new(txs[i], i);
            }

            Array.Sort(sortKeys, txExecutionOrder, lead, sortCount);
        }

        internal static void CancelIncompleteGasResults(GasValidationResultSlot[] gasResults, int length)
        {
            for (int i = 0; i < length; i++)
            {
                gasResults[i].TrySetCanceled();
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
            ulong cumulativeGas = 0;
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

        private static TxReceipt[] CombineReceipts(BlockReceiptsTracer[] receiptsTracers, int len)
        {
            TxReceipt[] result = new TxReceipt[len];
            ulong cumulativeGas = 0;
            for (int i = 0; i < len; i++)
            {
                result[i] = receiptsTracers[i].TxReceipts[0];
                result[i].Index = i;
                cumulativeGas += result[i].GasUsed;
                result[i].GasUsedTotal = cumulativeGas;
            }

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
            IBlockProcessor.IBlockTransactionsExecutor inner,
            in IntrinsicGas<EthereumGasPolicy> intrinsicGas)
        {
            long txStart = inner.StartTxTimer();
            TransactionResult result;
            try
            {
                result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider, in intrinsicGas);
            }
            finally
            {
                // Stop the timer even on failure so a slow-block log captures the failing tx's time
                inner.StopTxTimer(index, txStart);
            }
            if (!result) BlockValidationTransactionsExecutor.ThrowInvalidTransactionException(result, block.Header, currentTx, index);
        }

        /// <summary>Stable, allocation-free sort key for the tx-tail schedule. Sorts heaviest
        /// estimated-work transactions first; ties resolved by ascending tx index so the
        /// schedule is deterministic.</summary>
        private readonly struct TxExecutionSortKey(Transaction tx, int index) : IComparable<TxExecutionSortKey>
        {
            private readonly ulong _gasLimit = tx.GasLimit;
            private readonly int _dataLength = tx.DataLength;
            private readonly int _authorizationCount = tx.AuthorizationList?.Length ?? 0;
            private readonly int _accessListItems = GetAccessListItemCount(tx.AccessList);
            private readonly int _contractCreation = tx.IsContractCreation ? 1 : 0;
            private readonly int _index = index;

            public int CompareTo(TxExecutionSortKey other)
            {
                int comparison = other._gasLimit.CompareTo(_gasLimit);
                if (comparison != 0) return comparison;

                comparison = other._dataLength.CompareTo(_dataLength);
                if (comparison != 0) return comparison;

                comparison = other._authorizationCount.CompareTo(_authorizationCount);
                if (comparison != 0) return comparison;

                comparison = other._accessListItems.CompareTo(_accessListItems);
                if (comparison != 0) return comparison;

                comparison = other._contractCreation.CompareTo(_contractCreation);
                if (comparison != 0) return comparison;

                return _index.CompareTo(other._index);
            }

            private static int GetAccessListItemCount(AccessList? accessList)
            {
                if (accessList is null)
                {
                    return 0;
                }

                (int addressesCount, int storageKeysCount) = accessList.Count;
                return addressesCount + storageKeysCount;
            }
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
