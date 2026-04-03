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
using Nethermind.Blockchain.Find;
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
/// records state diffs, then applies diffs sequentially — re-executing on conflict.
/// </summary>
public class ParallelBlockValidationTransactionsExecutor : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly IWorldState _mainWorldState;
    private readonly ITransactionProcessorAdapter _mainTransactionProcessor;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockTree _blockTree;
    private readonly StateDiffRecorder _mainRecorder;
    private readonly ILogger _logger;
    private readonly Worker[] _workers;

    private Address _coinbaseAddress = Address.Zero;

    public ParallelBlockValidationTransactionsExecutor(
        ILifetimeScope rootScope,
        IWorldStateManager worldStateManager,
        IWorldState mainWorldState,
        ITransactionProcessorAdapter mainTransactionProcessor,
        ISpecProvider specProvider,
        IBlockTree blockTree,
        StateDiffRecorder mainRecorder,
        ILogManager logManager)
    {
        _mainWorldState = mainWorldState;
        _mainTransactionProcessor = mainTransactionProcessor;
        _specProvider = specProvider;
        _blockTree = blockTree;
        _mainRecorder = mainRecorder;
        _logger = logManager.GetClassLogger<ParallelBlockValidationTransactionsExecutor>();

        int parallelismFactor = Environment.ProcessorCount;
        _workers = new Worker[parallelismFactor];

        for (int i = 0; i < parallelismFactor; i++)
        {
            StateDiffRecorder recorder = new();
            IWorldStateScopeProvider readOnlyProvider = worldStateManager.CreateResettableWorldState();
            IWorldStateScopeProvider decorated = new StateDiffScopeProviderDecorator(readOnlyProvider, recorder, bufferOnly: true);

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
                recorder);
        }
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _coinbaseAddress = blockExecutionContext.Coinbase;

        _mainTransactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
        _mainRecorder.CoinbaseAddress = _coinbaseAddress;

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

        IReleaseSpec spec = _specProvider.GetSpec(block.Header);
        BlockHeader? parentHeader = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        if (parentHeader is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot find parent header for block {block.Number}, falling back to sequential execution");
            return FallbackSequentialExecution(block, processingOptions, receiptsTracer);
        }

        Stopwatch totalSw = Stopwatch.StartNew();

        // Phase 1 — Parallel execution
        Stopwatch parallelSw = Stopwatch.StartNew();
        ParallelTxResult[] results = ExecuteInParallel(block, parentHeader, spec, token);
        long parallelMs = parallelSw.ElapsedMilliseconds;

        // Phase 2 — Sequential conflict resolution + main state application
        Stopwatch sequentialSw = Stopwatch.StartNew();
        int conflictCount = ApplyResultsSequentially(block, results, spec, processingOptions, receiptsTracer);
        long sequentialMs = sequentialSw.ElapsedMilliseconds;

        long totalMs = totalSw.ElapsedMilliseconds;

        if (_logger.IsInfo)
            _logger.Info($"Parallel block execution: {txCount} txs, {conflictCount} conflicts, parallel={parallelMs}ms, sequential={sequentialMs}ms, total={totalMs}ms");

        return [.. receiptsTracer.TxReceipts];
    }

    private ParallelTxResult[] ExecuteInParallel(Block block, BlockHeader parentHeader, IReleaseSpec spec, CancellationToken token)
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
                    Transaction tx = block.Transactions[i];
                    ReceiptCapturingTracer tracer = new();
                    TransactionResult txResult;

                    using (worker.WorldState.BeginScope(parentHeader))
                    {
                        txResult = worker.TxProcessor.Execute(tx, tracer);
                        if ((bool)txResult)
                            worker.WorldState.Commit(spec, NullStateTracer.Instance);
                    }

                    results[i] = new ParallelTxResult(
                        (bool)txResult,
                        txResult,
                        tracer.Captured,
                        worker.Recorder.TakeDiff());
                }

                if (_logger.IsDebug)
                    _logger.Debug($"Parallel worker {workerIdx} finished in {workerSw.ElapsedMilliseconds}ms");
            }, token);
        }

        Task.WaitAll(tasks, token);
        return results;
    }

    private int ApplyResultsSequentially(
        Block block,
        ParallelTxResult[] results,
        IReleaseSpec spec,
        ProcessingOptions processingOptions,
        BlockReceiptsTracer receiptsTracer)
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

                // Re-execute on main world state; capture actual writes via main recorder
                _mainRecorder.Reset();
                TransactionResult result = _mainTransactionProcessor.ProcessTransaction(tx, receiptsTracer, processingOptions, _mainWorldState);
                if (!result)
                    ThrowInvalidTransactionException(result, block.Header, tx, i);

                TransactionStateDiff reExecDiff = _mainRecorder.TakeDiff();

                // Use ACTUAL writes from re-execution for committed set
                foreach (AddressAsKey addr in reExecDiff.AccountWrites.Keys)
                    committedWrites.Add(addr);
                foreach (StorageCell cell in reExecDiff.StorageWrites.Keys)
                    committedStorageWrites.Add(cell);
            }
            else
            {
                // Apply diff to main world state
                ApplyStateDiff(diff, spec);

                // Replay receipt into main receiptsTracer
                CapturedReceipt captured = parallel.Receipt;
                using ITxTracer tracer = receiptsTracer.StartNewTxTrace(tx);
                if (captured.IsSuccess)
                    receiptsTracer.MarkAsSuccess(captured.Recipient, captured.GasConsumed,
                        captured.Output, captured.Logs!, captured.StateRoot);
                else
                    receiptsTracer.MarkAsFailed(captured.Recipient, captured.GasConsumed,
                        captured.Output, captured.Error, captured.StateRoot);
                receiptsTracer.EndTxTrace();

                // Update committed write sets
                foreach (AddressAsKey addr in diff.AccountWrites.Keys)
                    committedWrites.Add(addr);
                foreach (StorageCell cell in diff.StorageWrites.Keys)
                    committedStorageWrites.Add(cell);
            }
        }

        return conflictCount;
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

        foreach (AddressAsKey key in diff.AccountWrites.Keys)
            if (committedWrites.Contains(key)) return true;

        if (diff.ReadStorageCells.Overlaps(committedStorageWrites))
            return true;

        foreach (StorageCell cell in diff.StorageWrites.Keys)
            if (committedStorageWrites.Contains(cell)) return true;

        return false;
    }

    private void ApplyStateDiff(TransactionStateDiff diff, IReleaseSpec spec)
    {
        _mainWorldState.TakeSnapshot(newTransactionStart: true);

        foreach (KeyValuePair<AddressAsKey, (Account? Before, Account? After)> kv in diff.AccountWrites)
        {
            Address address = kv.Key;
            Account? before = kv.Value.Before;
            Account? after = kv.Value.After;

            if (before is null && after is not null)
            {
                _mainWorldState.CreateAccount(address, after.Balance, after.Nonce);
            }
            else if (before is not null && after is null)
            {
                _mainWorldState.DeleteAccount(address);
            }
            else if (before is not null && after is not null)
            {
                if (after.Balance > before.Balance)
                    _mainWorldState.AddToBalance(address, after.Balance - before.Balance, spec, out _);
                else if (after.Balance < before.Balance)
                    _mainWorldState.SubtractFromBalance(address, before.Balance - after.Balance, spec, out _);

                if (after.Nonce > before.Nonce)
                    _mainWorldState.IncrementNonce(address, after.Nonce - before.Nonce, out _);
            }
        }

        foreach (KeyValuePair<StorageCell, byte[]> kv in diff.StorageWrites)
        {
            _mainWorldState.Set(kv.Key, kv.Value);
        }

        if (diff.CodeWrites.Count > 0)
        {
            Dictionary<ValueHash256, byte[]> codeByHash = [];
            foreach ((Address _, ValueHash256 codeHash, byte[] code) in diff.CodeWrites)
                codeByHash[codeHash] = code;

            foreach (KeyValuePair<AddressAsKey, (Account? Before, Account? After)> kv in diff.AccountWrites)
            {
                Account? after = kv.Value.After;
                Account? before = kv.Value.Before;
                if (after is not null && after.HasCode)
                {
                    ValueHash256 afterCodeHash = after.CodeHash;
                    if ((before is null || before.CodeHash != afterCodeHash) && codeByHash.TryGetValue(afterCodeHash, out byte[]? code))
                    {
                        _mainWorldState.InsertCode(kv.Key, afterCodeHash, code, spec);
                    }
                }
            }
        }

        if (diff.CoinbaseBalanceDelta > UInt256.Zero)
            _mainWorldState.AddToBalance(_coinbaseAddress, diff.CoinbaseBalanceDelta, spec, out _);

        _mainWorldState.Commit(spec, NullStateTracer.Instance);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction tx, int index) =>
        throw new InvalidTransactionException(header, $"Transaction {tx.Hash} at index {index} failed with error {result.ErrorDescription}", result);

    private TxReceipt[] FallbackSequentialExecution(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer)
    {
        Evm.Metrics.ResetBlockStats();

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction currentTx = block.Transactions[i];
            TransactionResult result = _mainTransactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _mainWorldState);
            if (!result)
                ThrowInvalidTransactionException(result, block.Header, currentTx, i);
        }

        return [.. receiptsTracer.TxReceipts];
    }

    private readonly record struct ParallelTxResult(bool Success, TransactionResult Result, CapturedReceipt Receipt, TransactionStateDiff Diff);
    private readonly record struct Worker(ILifetimeScope Scope, IWorldState WorldState, ITransactionProcessorAdapter TxProcessor, StateDiffRecorder Recorder);
}
