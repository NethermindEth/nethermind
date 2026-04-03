// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.Parallel;

/// <summary>
/// Parallel variant of <see cref="BlockProcessor.BlockValidationTransactionsExecutor"/>.
/// Executes all transactions in parallel on separate read-only world state copies,
/// records state diffs, then applies diffs sequentially at the scope provider level —
/// re-executing conflicting txs on the current accumulated state.
/// </summary>
public class ParallelBlockValidationTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ISpecProvider _specProvider;
    private readonly ParallelBlockExecutionContext _context;
    private readonly ILogger _logger;
    private readonly Worker[] _workers;

    private Address _coinbaseAddress = Address.Zero;

    public ParallelBlockValidationTransactionsExecutor(
        ILifetimeScope rootScope,
        IWorldStateManager worldStateManager,
        ISpecProvider specProvider,
        ParallelBlockExecutionContext context,
        ILogManager logManager)
    {
        _specProvider = specProvider;
        _context = context;
        _logger = logManager.GetClassLogger<ParallelBlockValidationTransactionsExecutor>();

        int parallelismFactor = Environment.ProcessorCount;
        _workers = new Worker[parallelismFactor];

        for (int i = 0; i < parallelismFactor; i++)
        {
            StateDiffRecorder recorder = new();
            IWorldStateScopeProvider readOnlyProvider = worldStateManager.CreateResettableWorldState();
            StateDiffScopeProviderDecorator decorated = new(readOnlyProvider, recorder, bufferOnly: true);

            ILifetimeScope childScope = rootScope.BeginLifetimeScope(builder =>
            {
                builder
                    .AddScoped<IWorldStateScopeProvider>(decorated)
                    .AddScoped<IWorldState, WorldState>()
                    .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>();
            });

            _workers[i] = new Worker(
                childScope,
                childScope.Resolve<IWorldState>(),
                childScope.Resolve<ITransactionProcessorAdapter>(),
                recorder,
                decorated);
        }

        if (_logger.IsInfo) _logger.Info($"Parallel block validation executor created with {parallelismFactor} workers");
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _coinbaseAddress = blockExecutionContext.Coinbase;

        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i].TxProcessor.SetBlockExecutionContext(in blockExecutionContext);
            _workers[i].Recorder.CoinbaseAddress = _coinbaseAddress;
        }
    }

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        Evm.Metrics.ResetBlockStats();

        int txCount = block.Transactions.Length;
        if (txCount == 0) return [];

        System.IO.File.AppendAllText("/tmp/parallel_debug.log", $"ProcessTransactions block={block.Number} txs={txCount}\n");

        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        // Use the base block captured by the context when BranchProcessor opened the main scope
        BlockHeader? parentHeader = _context.LastBaseBlock;

        Stopwatch totalSw = Stopwatch.StartNew();

        // Phase 1 — Parallel execution
        Stopwatch parallelSw = Stopwatch.StartNew();
        ParallelTxResult[] results = ExecuteInParallel(block, parentHeader, spec, token);
        long parallelMs = parallelSw.ElapsedMilliseconds;
        System.IO.File.AppendAllText("/tmp/parallel_debug.log", $"  Parallel phase done in {parallelMs}ms\n");

        // Phase 2 — Sequential conflict resolution + scope-level diff application
        Stopwatch sequentialSw = Stopwatch.StartNew();
        int conflictCount = ApplyResultsSequentially(block, results, spec, parentHeader, receiptsTracer, token);
        long sequentialMs = sequentialSw.ElapsedMilliseconds;
        System.IO.File.AppendAllText("/tmp/parallel_debug.log", $"  Sequential phase done in {sequentialMs}ms, conflicts={conflictCount}\n");

        long totalMs = totalSw.ElapsedMilliseconds;

        if (_logger.IsInfo)
            _logger.Info($"Parallel block execution: {txCount} txs, {conflictCount} conflicts, parallel={parallelMs}ms, sequential={sequentialMs}ms, total={totalMs}ms");

        return [.. receiptsTracer.TxReceipts];
    }

    private ParallelTxResult[] ExecuteInParallel(Block block, BlockHeader? parentHeader, IReleaseSpec spec, CancellationToken token)
    {
        int txCount = block.Transactions.Length;
        ParallelTxResult[] results = new ParallelTxResult[txCount];
        int workerCount = Math.Min(_workers.Length, txCount);

        Task[] tasks = new Task[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            int workerIdx = w;
            tasks[w] = Task.Run(() =>
            {
                Stopwatch workerSw = Stopwatch.StartNew();
                Worker worker = _workers[workerIdx];

                for (int i = workerIdx; i < txCount; i += workerCount)
                {
                    results[i] = ExecuteSingleTx(worker, block.Transactions[i], parentHeader, spec);
                }

                if (_logger.IsDebug)
                    _logger.Debug($"Parallel worker {workerIdx} finished in {workerSw.ElapsedMilliseconds}ms");
            }, token);
        }

        Task.WaitAll(tasks, token);
        return results;
    }

    private static ParallelTxResult ExecuteSingleTx(Worker worker, Transaction tx, BlockHeader? baseHeader, IReleaseSpec spec)
    {
        ReceiptCapturingTracer tracer = new();
        TransactionResult txResult;

        using (worker.WorldState.BeginScope(baseHeader))
        {
            txResult = worker.TxProcessor.Execute(tx, tracer);
            if ((bool)txResult)
                worker.WorldState.Commit(spec, NullStateTracer.Instance);
        }

        return new ParallelTxResult(
            (bool)txResult,
            txResult,
            tracer.Captured,
            worker.Recorder.TakeDiff());
    }

    private int ApplyResultsSequentially(
        Block block,
        ParallelTxResult[] results,
        IReleaseSpec spec,
        BlockHeader? parentHeader,
        BlockReceiptsTracer receiptsTracer,
        CancellationToken token)
    {
        HashSet<AddressAsKey> committedWrites = [];
        HashSet<StorageCell> committedStorageWrites = [];
        int conflictCount = 0;

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];
            ParallelTxResult parallel = results[i];
            TransactionStateDiff diff = parallel.Diff;

            bool hasConflict = !parallel.Success
                || HasConflict(diff, committedWrites, committedStorageWrites);

            Metrics.ParallelStateDiffMergeAttempts++;

            if (hasConflict)
            {
                conflictCount++;
                Metrics.ParallelStateDiffMergeConflicts++;

                // Re-execute on current accumulated state using a worker
                // Get current root hash (includes all previously applied diffs)
                Hash256 currentRoot = _context.GetCurrentRootHash();
                BlockHeader reExecHeader = new() { StateRoot = currentRoot };

                Worker reExecWorker = _workers[0]; // reuse first worker
                reExecWorker.Recorder.CoinbaseAddress = _coinbaseAddress;
                ParallelTxResult reExecResult = ExecuteSingleTx(reExecWorker, tx, reExecHeader, spec);

                if (!reExecResult.Success)
                    ThrowInvalidTransactionException(reExecResult.Result, block.Header, tx, i);

                // Apply re-execution diff at scope level
                _context.InjectDiff(reExecResult.Diff, _coinbaseAddress);

                // Replay receipt
                ReplayReceipt(reExecResult.Receipt, tx, receiptsTracer);

                // Track actual writes from re-execution
                foreach (AddressAsKey addr in reExecResult.Diff.WrittenAccounts)
                    committedWrites.Add(addr);
                foreach (StorageCell cell in reExecResult.Diff.WrittenStorageCells)
                    committedStorageWrites.Add(cell);
            }
            else
            {
                // Apply parallel diff at scope level
                _context.InjectDiff(diff, _coinbaseAddress);

                // Replay receipt
                ReplayReceipt(parallel.Receipt, tx, receiptsTracer);

                // Update committed write sets
                foreach (AddressAsKey addr in diff.WrittenAccounts)
                    committedWrites.Add(addr);
                foreach (StorageCell cell in diff.WrittenStorageCells)
                    committedStorageWrites.Add(cell);
            }
        }

        return conflictCount;
    }

    private static void ReplayReceipt(CapturedReceipt captured, Transaction tx, BlockReceiptsTracer receiptsTracer)
    {
        using ITxTracer tracer = receiptsTracer.StartNewTxTrace(tx);
        if (captured.IsSuccess)
            receiptsTracer.MarkAsSuccess(captured.Recipient, captured.GasConsumed,
                captured.Output, captured.Logs!, captured.StateRoot);
        else
            receiptsTracer.MarkAsFailed(captured.Recipient, captured.GasConsumed,
                captured.Output, captured.Error, captured.StateRoot);
        receiptsTracer.EndTxTrace();
    }

    private static bool HasConflict(
        TransactionStateDiff diff,
        HashSet<AddressAsKey> committedWrites,
        HashSet<StorageCell> committedStorageWrites)
    {
        if (committedWrites.Count == 0 && committedStorageWrites.Count == 0)
            return false;

        if (diff.ReadAccounts.Overlaps(committedWrites))
            return true;

        foreach (AddressAsKey key in diff.WrittenAccounts)
            if (committedWrites.Contains(key)) return true;

        if (diff.ReadStorageCells.Overlaps(committedStorageWrites))
            return true;

        foreach (StorageCell cell in diff.WrittenStorageCells)
            if (committedStorageWrites.Contains(cell)) return true;

        return false;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction tx, int index) =>
        throw new InvalidTransactionException(header, $"Transaction {tx.Hash} at index {index} failed with error {result.ErrorDescription}", result);

    private readonly record struct ParallelTxResult(bool Success, TransactionResult Result, CapturedReceipt Receipt, TransactionStateDiff Diff);
    private readonly record struct Worker(
        ILifetimeScope Scope,
        IWorldState WorldState,
        ITransactionProcessorAdapter TxProcessor,
        StateDiffRecorder Recorder,
        StateDiffScopeProviderDecorator Decorator);
}
