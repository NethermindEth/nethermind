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
/// records state diffs, then merges diffs into an in-memory overlay sequentially —
/// re-executing conflicting txs on a worker that sees the overlay.
/// </summary>
public class ParallelBlockValidationTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ISpecProvider _specProvider;
    private readonly ParallelBlockExecutionContext _context;
    private readonly ILogger _logger;
    private readonly Worker[] _workers;

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
        for (int i = 0; i < _workers.Length; i++)
            _workers[i].TxProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        Evm.Metrics.ResetBlockStats();
        _context.AccountOverlay.Clear();
        _context.StorageOverlay.Clear();
        _context.CodeOverlay.Clear();

        int txCount = block.Transactions.Length;
        if (txCount == 0) return [];

        IReleaseSpec spec = _specProvider.GetSpec(block.Header);
        BlockHeader? parentHeader = _context.LastBaseBlock;

        Stopwatch totalSw = Stopwatch.StartNew();

        // Phase 1 — Parallel execution
        Stopwatch parallelSw = Stopwatch.StartNew();
        ParallelTxResult[] results = ExecuteInParallel(block, parentHeader, spec, token);
        long parallelMs = parallelSw.ElapsedMilliseconds;

        // Phase 2 — Sequential: merge diffs into overlay, re-execute conflicts
        Stopwatch sequentialSw = Stopwatch.StartNew();
        int conflictCount = ApplyResultsSequentially(block, results, spec, parentHeader, receiptsTracer, token);
        long sequentialMs = sequentialSw.ElapsedMilliseconds;

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
                Worker worker = _workers[workerIdx];
                for (int i = workerIdx; i < txCount; i += workerCount)
                    results[i] = ExecuteSingleTx(worker, block.Transactions[i], parentHeader, spec);
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

                // Re-execute on a worker that sees the overlay (accumulated state)
                Worker reExecWorker = _workers[0];
                ParallelTxResult reExecResult = ExecuteSingleTxWithOverlay(reExecWorker, tx, parentHeader, spec);

                if (!reExecResult.Success)
                    ThrowInvalidTransactionException(reExecResult.Result, block.Header, tx, i);

                // Merge re-execution diff into overlay
                _context.MergeDiff(reExecResult.Diff);

                ReplayReceipt(reExecResult.Receipt, tx, receiptsTracer);

                foreach (AddressAsKey addr in reExecResult.Diff.WrittenAccounts)
                    committedWrites.Add(addr);
                foreach (StorageCell cell in reExecResult.Diff.WrittenStorageCells)
                    committedStorageWrites.Add(cell);
            }
            else
            {
                // Merge parallel diff into overlay
                _context.MergeDiff(diff);

                ReplayReceipt(parallel.Receipt, tx, receiptsTracer);

                foreach (AddressAsKey addr in diff.WrittenAccounts)
                    committedWrites.Add(addr);
                foreach (StorageCell cell in diff.WrittenStorageCells)
                    committedStorageWrites.Add(cell);
            }
        }

        return conflictCount;
    }

    /// <summary>
    /// Execute a tx on a worker whose scope reads from the overlay (accumulated diffs)
    /// before falling through to the read-only parent state.
    /// </summary>
    private ParallelTxResult ExecuteSingleTxWithOverlay(Worker worker, Transaction tx, BlockHeader? parentHeader, IReleaseSpec spec)
    {
        // Temporarily layer the overlay onto the worker's scope provider
        worker.Decorator.SetOverlay(_context);
        try
        {
            return ExecuteSingleTx(worker, tx, parentHeader, spec);
        }
        finally
        {
            worker.Decorator.ClearOverlay();
        }
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
