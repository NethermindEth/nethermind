// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
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
        : IBlockProcessor.IBlockTransactionsExecutor,
          BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler
    {
        private readonly ILogger _logger = logManager.GetClassLogger<ParallelBlockValidationTransactionsExecutor>();
        private readonly List<TxProcessedEventArgs>? _pendingTransactionProcessedEvents =
            transactionProcessedEventHandler is null ? null : [];
        private ExecutedGasTally? _executedGasTally;
        private CancellationTokenSource _rejection = new();
        private BlockReceiptsTracer[] _receiptsTracerPool = [];
        private GasValidationResult[] _gasResultPool = [];
        private int[] _txExecutionOrder = [];
        private TxExecutionSortKey[] _txExecutionSortKeys = [];
        private int _pooledSlotsInUse;

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

            ClearTransactionProcessedEvents();
            _pendingTransactionProcessedEvents?.EnsureCapacity(block.Transactions.Length);
            inner.SetupTxTimingMetrics(block);

            TxReceipt[] receipts = !ZkEvmFlag.IsActive && !block.IsGenesis && balManager.ParallelExecutionEnabled
                ? ProcessTransactionsParallel(block, processingOptions, receiptsTracer, token)
                : ProcessTransactionsSequential(block, processingOptions, receiptsTracer, token);

            return receipts;
        }

        private TxReceipt[] ProcessTransactionsSequential(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
        {
            bool shouldValidate = !processingOptions.ContainsFlag(ProcessingOptions.NoValidation);
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            ulong totalExecutionGas = 0;
            ulong totalStateGas = 0;

            balManager.NextTransaction();

            for (uint i = 0; i < block.Transactions.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                Transaction currentTx = block.Transactions[i];
                ITransactionProcessorAdapter txProcessor = balManager.GetTxProcessor(i + 1);
                if (shouldValidate)
                {
                    // A simulate no-gas call defaults to GasCap, which the inclusion check would reject;
                    // resolve it to the remaining block budget (state dimension too) before that check runs.
                    txProcessor.PrepareForInclusionCheck(currentTx, block.Header.GasLimit.SaturatingSub(totalStateGas));
                    BlockAccessListManager.CheckPerTxInclusion(block, (int)i, currentTx, spec, totalExecutionGas, totalStateGas);
                }

                ProcessTransaction(txProcessor, stateProvider, block, currentTx, (int)i, receiptsTracer, processingOptions, inner);
                totalExecutionGas = receiptsTracer.CumulativeExecutionGasUsed;
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

                if (_pendingTransactionProcessedEvents is not null && transactionProcessedEventHandler!.HasSubscribers)
                {
                    _pendingTransactionProcessedEvents.Add(
                        new TxProcessedEventArgs((int)i, currentTx, block.Header, receiptsTracer.TxReceipts[(int)i]));
                }
            }

            return [.. receiptsTracer.TxReceipts];
        }

        private TxReceipt[] ProcessTransactionsParallel(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer outerReceiptsTracer, CancellationToken token)
        {
            using ParallelUnbalancedWork.WorkerScope workers = ParallelUnbalancedWork.BeginWorkerScope(Environment.ProcessorCount);
            int len = block.Transactions.Length;
            bool isBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
            IBlockTracer parallelSafeTracer = GetParallelSafeTracer(outerReceiptsTracer.OtherTracer);
            EnsureParallelBuffers(len);
            BlockReceiptsTracer[] receiptsTracers = _receiptsTracerPool;
            GasValidationResult[] gasResults = _gasResultPool;
            for (int i = 0; i < len; i++)
            {
                receiptsTracers[i].ResetForParallelTx(block, parallelSafeTracer);
                gasResults[i] = default;
            }

            // Release the slots the previous block used but this one does not. Left alone they keep
            // that block's receipts, the block itself and any InvalidBlockException in the gas slot;
            // resetting them like the active ones would only swap the current block in. The pool
            // never shrinks, so this runs once per shrink rather than every block.
            for (int i = len; i < _pooledSlotsInUse; i++)
            {
                receiptsTracers[i].ReleaseForPooling();
                gasResults[i] = default;
            }

            _pooledSlotsInUse = len;

            // Cancelled once the block is rejected; a source that was cancelled cannot be reset, so it is replaced.
            if (!_rejection.TryReset()) _rejection = new();
            CancellationTokenSource rejection = _rejection;
            ExecutedGasTally executedGas = _executedGasTally ??= new();
            executedGas.Reset(rejection);
            BuildTxExecutionOrder(block.Transactions, _txExecutionOrder, _txExecutionSortKeys, GetCanonicalExecutionLead(len));

            try
            {
                // balIndex 0 (pre-execution) is held by main-thread system contract handlers; release its processor to the workers.
                balManager.ReturnTxProcessor(0u);

                // Iterations: 0 = ApplyBal, 1..len = tx (scheduled order =
                // _txExecutionOrder[i-1]; balIndex = scheduledTxIndex+1). Pre-execution
                // (StoreBeaconRoot + ApplyBlockhashStateChanges) ran sequentially in
                // BlockProcessor.ProcessBlock before this method was called.
                ParallelUnbalancedWork.For(
                    0,
                    len + 1,
                    // Cancelled, the workers stop pulling iterations and the loop throws once they have joined.
                    token.CanBeCanceled
                        ? new ParallelOptions { MaxDegreeOfParallelism = ParallelUnbalancedWork.DefaultOptions.MaxDegreeOfParallelism, CancellationToken = token }
                        : ParallelUnbalancedWork.DefaultOptions,
                    new ParallelTxLoopState(block, processingOptions, stateProvider, balManager, receiptsTracers, gasResults,
                        specProvider, _txExecutionOrder, isBlockProcessingThread, inner, executedGas, rejection.Token),
                    static (i, state) =>
                    {
                        // Propagate the parent thread's IsBlockProcessingThread flag onto the
                        // worker so processing-stats heuristics (e.g. allocation-thread filters)
                        // continue to attribute work correctly across the parallel boundary.
                        bool previousIsBlockProcessingThread = ProcessingThread.IsBlockProcessingThread;
                        ProcessingThread.IsBlockProcessingThread = state.IsBlockProcessingThread;
                        try
                        {
                            if (i == 0)
                            {
                                // The list is the block's own claim, so a rejected block stops applying it between steps;
                                // its scope is discarded, and disposing it cancels any warm-up still running.
                                if (state.IsBlockRejected) return state;
                                state.BalManager.WaitForBalWarmup();
                                if (state.IsBlockRejected) return state;
                                ReadOnlyBlockAccessList bal = state.Block.BlockAccessList;
                                // The world state never sees the BAL's writes, so its account changes would miss them.
                                if (state.IsBlockProcessingThread) state.Block.AccountChanges = bal.GetStateChangedAddresses();
                                // Pre-block writes on the shared state (e.g. AuRa's system accounts) are only committed to
                                // the journal; flush them to the scope before the BAL's values are laid over them.
                                state.StateProvider.Commit(state.SpecProvider.GetSpec(state.Block.Header));
                                state.StateProvider.ApplyBal(bal, state.Rejection);
                                if (state.IsBlockRejected) return state;
                                state.StateProvider.RecalculateStateRoot();
                                return state;
                            }

                            int txIndex = state.TxExecutionOrder[i - 1];
                            Transaction tx = state.Txs[txIndex];
                            // These stops apply before a tx starts, so a worker already executing one finishes it:
                            // up to one tx per worker, each at most 2^24 execution gas (EIP-7825), still runs.
                            if (state.ExecutedGas.Failure is { } failure)
                            {
                                // The gas checks throw at or before the failed slot, so the rest cannot change the
                                // outcome. Reusing the failure makes the rejection name the failing tx even when this
                                // slot comes before it.
                                state.GasResults[txIndex] = new GasValidationResult(0, 0, failure);
                                return state;
                            }

                            if (state.ExecutedGas.ExceedsLimit)
                            {
                                // Fill the slot without executing the tx.
                                state.GasResults[txIndex] = new GasValidationResult(0, 0, new InvalidBlockException(state.Block,
                                    $"Block gas limit exceeded: transactions executed out of order already exceed block gas limit {state.Block.Header.GasLimit}; transaction index {txIndex} not executed."));
                                return state;
                            }

                            try
                            {
                                // The using block recycles the pool slot via Dispose even if ProcessTransaction throws;
                                // what the index recorded stays in the processor's log.
                                using (TxProcessorLease lease = state.BalManager.RentTxProcessor((uint)(txIndex + 1)))
                                {
                                    ProcessTransaction(
                                        lease.Adapter,
                                        state.StateProvider,
                                        state.Block,
                                        tx,
                                        txIndex,
                                        state.ReceiptsTracers[txIndex],
                                        state.ProcessingOptions,
                                        state.Inner);
                                }
                                ulong blockStateGasUsed = state.ReceiptsTracers[txIndex].BlockStateGasUsed;
                                state.ExecutedGas.Add(tx.BlockGasUsed, blockStateGasUsed, state.Block.Header.GasLimit);
                                state.GasResults[txIndex] = new GasValidationResult(tx.BlockGasUsed, blockStateGasUsed, null);
                            }
                            catch (InvalidBlockException ex)
                            {
                                // A rejected tx contributes nothing to block accumulators —
                                // the sequential path never reaches gas accounting for it because
                                // the exception bubbles up immediately. IncrementalValidation also
                                // rethrows on `ex is not null` before doing any accounting, so the
                                // tuple values here are observed only as cross-mode telemetry; we
                                // still report (0, 0) so any future consumer agrees with sequential.
                                state.ExecutedGas.Fail(ex);
                                state.GasResults[txIndex] = new GasValidationResult(0, 0, ex);
                            }

                            return state;
                        }
                        finally
                        {
                            ProcessingThread.IsBlockProcessingThread = previousIsBlockProcessingThread;
                        }
                    });

                // The workers have joined, so every slot holds its transaction's result.
                balManager.IncrementalValidation(
                    block,
                    gasResults,
                    receiptsTracers,
                    transactionProcessedEventHandler is { HasSubscribers: true } ? this : null,
                    token);
            }
            finally
            {
                // Always populate the outer tracer with per-tx receipts so the parallel path's
                // tracer state matches what the sequential path would have produced. Needed for
                // BlockTraceDumper's invalid-block dump on failure, and the success path returns
                // the harvested outer receipts.
                HarvestPerTxReceiptsIntoOuter(receiptsTracers, len, outerReceiptsTracer);
            }

            return [.. outerReceiptsTracer.TxReceipts];
        }

        void BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler.OnTransactionProcessed(TxProcessedEventArgs args) =>
            _pendingTransactionProcessedEvents?.Add(args);

        bool BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler.HasSubscribers =>
            transactionProcessedEventHandler?.HasSubscribers ?? false;

        /// <inheritdoc/>
        public void PublishTransactionProcessedEvents()
        {
            inner.PublishTransactionProcessedEvents();

            if (_pendingTransactionProcessedEvents is null)
            {
                return;
            }

            foreach (TxProcessedEventArgs pending in _pendingTransactionProcessedEvents)
            {
                transactionProcessedEventHandler!.OnTransactionProcessed(pending);
            }
        }

        /// <inheritdoc/>
        public void ClearTransactionProcessedEvents()
        {
            _pendingTransactionProcessedEvents?.Clear();
            inner.ClearTransactionProcessedEvents();
        }

        private void EnsureParallelBuffers(int length)
        {
            int currentLength = _receiptsTracerPool.Length;
            if (currentLength >= length)
            {
                return;
            }

            // Resize (rather than allocate fresh) preserves the BlockReceiptsTracer instances
            // already pooled in slots [0, currentLength); freshly allocated arrays would force
            // re-instantiation of every slot every block.
            int newLength = Math.Max(length, currentLength == 0 ? 4 : currentLength * 2);
            BlockReceiptsTracer[] receiptsTracerPool = _receiptsTracerPool;
            GasValidationResult[] gasResultPool = _gasResultPool;
            int[] txExecutionOrder = _txExecutionOrder;
            TxExecutionSortKey[] txExecutionSortKeys = _txExecutionSortKeys;
            Array.Resize(ref receiptsTracerPool, newLength);
            Array.Resize(ref gasResultPool, newLength);
            Array.Resize(ref txExecutionOrder, newLength);
            Array.Resize(ref txExecutionSortKeys, newLength);
            for (int i = currentLength; i < newLength; i++)
            {
                receiptsTracerPool[i] = new BlockReceiptsTracer(true);
            }

            _receiptsTracerPool = receiptsTracerPool;
            _gasResultPool = gasResultPool;
            _txExecutionOrder = txExecutionOrder;
            _txExecutionSortKeys = txExecutionSortKeys;
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
                receipt.Index = i;
                cumulativeGas += receipt.GasUsed;
                receipt.GasUsedTotal = cumulativeGas;
                outer.SetReceipt(i, receipt);
            }
        }

        private static void ProcessTransaction(
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider,
            Block block,
            Transaction currentTx,
            int index,
            BlockReceiptsTracer receiptsTracer,
            ProcessingOptions processingOptions,
            IBlockProcessor.IBlockTransactionsExecutor inner)
        {
            long txStart = inner.StartTxTimer();
            TransactionResult result;
            try
            {
                result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
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

        /// <summary>Inputs shared by every iteration of one block's parallel transaction loop.</summary>
        /// <remarks>
        /// <see cref="ParallelUnbalancedWork"/> passes the loop state by value into and out of each iteration, so a
        /// reference type keeps that to a pointer copy instead of copying every field.
        /// </remarks>
        private sealed class ParallelTxLoopState(
            Block block,
            ProcessingOptions processingOptions,
            IWorldState stateProvider,
            IBlockAccessListManager balManager,
            BlockReceiptsTracer[] receiptsTracers,
            GasValidationResult[] gasResults,
            ISpecProvider specProvider,
            int[] txExecutionOrder,
            bool isBlockProcessingThread,
            IBlockProcessor.IBlockTransactionsExecutor inner,
            ExecutedGasTally executedGas,
            CancellationToken rejection)
        {
            public readonly Block Block = block;
            public readonly ProcessingOptions ProcessingOptions = processingOptions;
            public readonly IWorldState StateProvider = stateProvider;
            public readonly IBlockAccessListManager BalManager = balManager;
            public readonly BlockReceiptsTracer[] ReceiptsTracers = receiptsTracers;
            public readonly GasValidationResult[] GasResults = gasResults;
            public readonly ISpecProvider SpecProvider = specProvider;
            public readonly Transaction[] Txs = block.Transactions;
            public readonly int[] TxExecutionOrder = txExecutionOrder;
            public readonly bool IsBlockProcessingThread = isBlockProcessingThread;
            public readonly IBlockProcessor.IBlockTransactionsExecutor Inner = inner;
            public readonly ExecutedGasTally ExecutedGas = executedGas;
            public readonly CancellationToken Rejection = rejection;

            /// <summary>Whether the block is already known to be invalid, so nothing still to run can save it.</summary>
            public bool IsBlockRejected => Rejection.IsCancellationRequested;
        }

        /// <summary>Block gas used by the transactions executed so far, in whatever order they ran, and the first
        /// transaction failure among them.</summary>
        /// <remarks>
        /// A transaction executed against the block access list uses the gas it uses canonically unless the list is
        /// wrong, so once the executed transactions alone exceed the gas limit in either EIP-8037 dimension the block
        /// is invalid. A failed transaction spends no block gas, yet the gas checks throw once they reach that slot.
        /// Stopping at either bounds the work an invalid block gets out of the workers before the gas checks run.
        /// </remarks>
        private sealed class ExecutedGasTally
        {
            private ulong _executionGas;
            private ulong _stateGas;
            private volatile bool _exceedsLimit;
            private InvalidBlockException? _failure;
            private CancellationTokenSource? _rejection;

            public bool ExceedsLimit => _exceedsLimit;

            public InvalidBlockException? Failure => Volatile.Read(ref _failure);

            public void Reset(CancellationTokenSource rejection)
            {
                _rejection = rejection;
                _executionGas = 0;
                _stateGas = 0;
                _exceedsLimit = false;
                _failure = null;
            }

            public void Fail(InvalidBlockException exception)
            {
                Interlocked.CompareExchange(ref _failure, exception, null);
                _rejection!.Cancel();
            }

            public void Add(ulong executionGas, ulong stateGas, ulong gasLimit)
            {
                // Non-short-circuiting | so both dimensions always accumulate.
                if (Interlocked.Add(ref _executionGas, executionGas) > gasLimit | Interlocked.Add(ref _stateGas, stateGas) > gasLimit)
                {
                    _exceedsLimit = true;
                    _rejection!.Cancel();
                }
            }
        }
    }
}
