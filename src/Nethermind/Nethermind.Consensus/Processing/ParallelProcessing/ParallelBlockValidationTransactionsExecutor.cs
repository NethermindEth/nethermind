// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelBlockValidationTransactionsExecutor(
    ITransactionProcessorAdapter transactionProcessorAdapter,
    ParallelEnvFactory parallelEnvFactory,
    PreBlockCaches preBlockCaches,
    IBlockFinder blockFinder,
    IWorldState stateProvider,
    ILogManager logManager,
    ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ObjectPool<HashSet<int>> _setPool = new DefaultObjectPool<HashSet<int>>(new DefaultPooledObjectPolicy<HashSet<int>>());
    private BlockExecutionContext _blockExecutionContext;
    private readonly ILogger _logger = logManager.GetClassLogger<ParallelBlockValidationTransactionsExecutor>();

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
    {
        Transaction[] transactions = block.Transactions;
        int txCount = transactions.Length;
        TxReceipt[] receipts = new TxReceipt[txCount];
        TransactionResult[] results = new TransactionResult[txCount];
        bool processedSuccessfully = false;
        OffParallelTrace trace = OffParallelTrace.Instance;
        MultiVersionMemory multiVersionMemory = new(txCount, trace);
        ParallelBlockMetricsCollector blockMetrics = new(txCount);
        FeeAccumulator feeAccumulator = new(txCount, block.Header.GasBeneficiary, _blockExecutionContext.Spec.FeeCollector);
        ParallelScheduler scheduler = new(txCount, trace, _setPool);
        ParallelUnbalancedWork.For(1, txCount, i => FindNonceDependencies(i, block, scheduler));
        BlockHeader parent = blockFinder.FindParentHeader(block.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        ParallelTransactionProcessor parallelTransactionProcessor = new(block, parent, parallelEnvFactory, multiVersionMemory, feeAccumulator, preBlockCaches, receipts, results, in _blockExecutionContext);
        try
        {
            using ParallelRunner parallelRunner = new(scheduler, multiVersionMemory, trace, parallelTransactionProcessor, 4, blockMetrics);
            parallelRunner.Run().GetAwaiter().GetResult();
            ThrowIfInvalidResults(block, transactions, results);
            FinalizeGasUsed(block, receipts);
            FeeRecipientWriteInfo feeRecipientWrites = PushChanges(stateProvider, multiVersionMemory, feeAccumulator, txCount);
            ApplyAccumulatedFees(stateProvider, feeAccumulator, _blockExecutionContext.Spec, feeRecipientWrites);
            RaiseTransactionProcessedEvents(block, transactions, receipts);
            BlockReceiptsTracer.AccumulateBlockBloom(block, receipts);
            processedSuccessfully = true;
            return receipts;
        }
        finally
        {
            ParallelBlockMetrics snapshot = blockMetrics.Snapshot();
            Metrics.ReportBlock(snapshot);
            LogParallelBlockReport(block, snapshot, results, processedSuccessfully);
        }
    }

    private void FindNonceDependencies(int txIndex, Block block, ParallelScheduler scheduler)
    {
        Address? sender = block.Transactions[txIndex].SenderAddress;
        if (sender is not null)
        {
            for (int i = txIndex - 1; i >= 0; i--)
            {
                Transaction prevTx = block.Transactions[i];
                if (prevTx.SenderAddress == sender)
                {
                    scheduler.AbortExecution(txIndex, i, false);
                    return;
                }

                if (prevTx.HasAuthorizationList)
                {
                    foreach (AuthorizationTuple tuple in prevTx.AuthorizationList)
                    {
                        // how to handle wrong authorizations?
                        if (tuple.Authority == sender)
                        {
                            scheduler.AbortExecution(txIndex, i, false);
                            return;
                        }
                    }
                }
            }
        }
    }

    private FeeRecipientWriteInfo PushChanges(IWorldState worldState, MultiVersionMemory multiVersionMemory, FeeAccumulator feeAccumulator, int txCount)
    {
        HashSet<Address> storageTouched = new();
        Address? gasBeneficiary = feeAccumulator.GasBeneficiary;
        Address? feeCollector = feeAccumulator.FeeCollector;
        int gasBeneficiaryLastWrite = -1;
        int feeCollectorLastWrite = -1;
        for (int txIndex = 0; txIndex < txCount; txIndex++)
        {
            Dictionary<Address, Account?>? accountUpdates = null;
            HashSet<Address>? storageClears = null;
            List<(StorageCell Cell, byte[] Value)>? storageWrites = null;

            foreach (KeyValuePair<ParallelStateKey, MultiVersionMemory.Value> write in multiVersionMemory.GetFinalWriteSet(txIndex))
            {
                if (write.Value.IsEstimate)
                {
                    continue;
                }

                ParallelStateKey key = write.Key;
                if (key.Kind != ParallelStateKeyKind.Storage)
                {
                    continue;
                }

                StorageCell cell = key.StorageCell;
                object value = write.Value.Data;
                if (ReferenceEquals(value, MultiVersionMemory.SelfDestructMonit))
                {
                    (storageClears ??= new HashSet<Address>()).Add(cell.Address);
                    continue;
                }

                if (value is byte[] bytes)
                {
                    (storageWrites ??= new List<(StorageCell Cell, byte[] Value)>()).Add((cell, bytes));
                    continue;
                }

                if (value is Account account)
                {
                    (accountUpdates ??= new Dictionary<Address, Account?>())[cell.Address] = account;
                    continue;
                }

                if (value is null)
                {
                    (accountUpdates ??= new Dictionary<Address, Account?>())[cell.Address] = null;
                }
            }

            if (storageClears is not null)
            {
                foreach (Address address in storageClears)
                {
                    bool accountDeleted = accountUpdates?.TryGetValue(address, out Account? _) ?? false;
                    // Avoid clearing when it's just an empty-base hint; keep prior tx writes intact.
                    if (accountDeleted || !storageTouched.Contains(address))
                    {
                        worldState.ClearStorage(address);
                        storageTouched.Add(address);
                    }
                }
            }

            if (storageWrites is not null)
            {
                foreach ((StorageCell Cell, byte[] Value) write in storageWrites)
                {
                    worldState.Set(write.Cell, write.Value);
                    storageTouched.Add(write.Cell.Address);
                }
            }

            if (accountUpdates is not null)
            {
                foreach (KeyValuePair<Address, Account?> accountUpdate in accountUpdates)
                {
                    worldState.SetAccount(accountUpdate.Key, accountUpdate.Value);
                    if (gasBeneficiary is not null && accountUpdate.Key == gasBeneficiary)
                    {
                        gasBeneficiaryLastWrite = txIndex;
                    }

                    if (feeCollector is not null && accountUpdate.Key == feeCollector)
                    {
                        feeCollectorLastWrite = txIndex;
                    }
                }
            }
        }

        return new FeeRecipientWriteInfo(gasBeneficiaryLastWrite, feeCollectorLastWrite);
    }

    private static void ApplyAccumulatedFees(IWorldState worldState, FeeAccumulator feeAccumulator, IReleaseSpec spec, FeeRecipientWriteInfo feeRecipientWrites)
    {
        Address? gasBeneficiary = feeAccumulator.GasBeneficiary;
        if (gasBeneficiary is not null && feeAccumulator.HasGasBeneficiaryPayments)
        {
            UInt256 total = feeAccumulator.GetTotalFees(gasBeneficiary);
            UInt256 applied = feeRecipientWrites.GasBeneficiaryLastWrite >= 0
                ? feeAccumulator.GetAccumulatedFees(gasBeneficiary, feeRecipientWrites.GasBeneficiaryLastWrite)
                : UInt256.Zero;
            UInt256 delta = total - applied;
            if (!delta.IsZero || feeRecipientWrites.GasBeneficiaryLastWrite < 0)
            {
                worldState.AddToBalanceAndCreateIfNotExists(gasBeneficiary, delta, spec);
            }
        }

        if (feeAccumulator.FeeCollector is { } feeCollector && feeCollector != gasBeneficiary)
        {
            UInt256 total = feeAccumulator.GetTotalFees(feeCollector);
            if (!total.IsZero)
            {
                UInt256 applied = feeRecipientWrites.FeeCollectorLastWrite >= 0
                    ? feeAccumulator.GetAccumulatedFees(feeCollector, feeRecipientWrites.FeeCollectorLastWrite)
                    : UInt256.Zero;
                UInt256 delta = total - applied;
                if (!delta.IsZero)
                {
                    worldState.AddToBalanceAndCreateIfNotExists(feeCollector, delta, spec);
                }
            }
        }
    }

    private readonly record struct FeeRecipientWriteInfo(int GasBeneficiaryLastWrite, int FeeCollectorLastWrite);

    private static void FinalizeGasUsed(Block block, TxReceipt[] receipts)
    {
        long gasUsed = 0;
        long gasLimit = block.Header.GasLimit;
        Transaction[] transactions = block.Transactions;

        for (int i = 0; i < receipts.Length; i++)
        {
            Transaction transaction = transactions[i];
            long remainingGas = gasLimit - gasUsed;
            if (transaction.GasLimit > remainingGas)
            {
                InvalidTransactionException.ThrowInvalidTransactionException(
                    TransactionResult.BlockGasLimitExceeded,
                    block.Header,
                    transaction,
                    i);
            }

            gasUsed += receipts[i].GasUsed;
            receipts[i].GasUsedTotal = gasUsed;
        }

        block.Header.GasUsed = gasUsed;
    }

    private static void ThrowIfInvalidResults(Block block, Transaction[] transactions, TransactionResult[] results)
    {
        for (int i = 0; i < results.Length; i++)
        {
            TransactionResult result = results[i];
            if (!result)
            {
                InvalidTransactionException.ThrowInvalidTransactionException(result, block.Header, transactions[i], i);
            }
        }
    }

    private void RaiseTransactionProcessedEvents(Block block, Transaction[] transactions, TxReceipt[] receipts)
    {
        if (transactionProcessedEventHandler is not null)
        {
            for (int i = 0; i < receipts.Length; i++)
            {
                transactionProcessedEventHandler.OnTransactionProcessed(new TxProcessedEventArgs(i, transactions[i], block.Header, receipts[i]));
            }
        }
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        transactionProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
        _blockExecutionContext = blockExecutionContext;
    }

    private void LogParallelBlockReport(Block block, in ParallelBlockMetrics snapshot, TransactionResult[] results, bool processedSuccessfully)
    {
        if (!_logger.IsInfo)
        {
            return;
        }

        int failedCount = 0;
        int firstFailedIndex = -1;
        TransactionResult firstFailedResult = default;

        for (int i = 0; i < results.Length; i++)
        {
            if (results[i])
            {
                continue;
            }

            failedCount++;
            if (firstFailedIndex < 0)
            {
                firstFailedIndex = i;
                firstFailedResult = results[i];
            }
        }

        string status = processedSuccessfully
            ? "OK"
            : failedCount > 0
                ? $"FAIL {failedCount} first={firstFailedIndex} {firstFailedResult.ErrorDescription}"
                : "FAIL (exception)";

        _logger.Info($"Parallel block {block.Number,10} | txs {snapshot.TxCount,6} | gas {block.Header.GasUsed,10:N0} | reexec {snapshot.Reexecutions,5} | reval {snapshot.Revalidations,5} | blocked {snapshot.BlockedReads,5} | parallel {snapshot.ParallelizationPercent,3}% | {status}");
    }
}

public class ParallelTransactionProcessor(
    Block block,
    BlockHeader parentBlock,
    ParallelEnvFactory parallelEnvFactory,
    MultiVersionMemory multiVersionMemory,
    FeeAccumulator feeAccumulator,
    PreBlockCaches preBlockCaches,
    TxReceipt[] receipts,
    TransactionResult[] results,
    in BlockExecutionContext blockExecutionContext) : IParallelTransactionProcessor<ParallelStateKey, object>
{
    private readonly ObjectPool<BlockReceiptsTracer> _tracers = new DefaultObjectPool<BlockReceiptsTracer>(new DefaultPooledObjectPolicy<BlockReceiptsTracer>());
    private readonly BlockExecutionContext _blockExecutionContext = blockExecutionContext;

    public Status TryExecute(Version version, out int? blockingTx, out bool wroteNewLocation)
    {
        int txIndex = version.TxIndex;
        blockingTx = null;
        wroteNewLocation = false;
        Transaction transaction = block.Transactions[txIndex];

        feeAccumulator.ClearFee(txIndex);
        using ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv env = parallelEnvFactory.Create(version, multiVersionMemory, feeAccumulator, preBlockCaches);
        using IReadOnlyTxProcessingScope scope = env.Build(parentBlock);
        ITransactionProcessor transactionProcessor = scope.TransactionProcessor;

        BlockReceiptsTracer tracer = _tracers.Get();
        tracer.StartNewBlockTrace(block);
        ITxTracer txTracer = tracer.StartNewTxTrace(transaction);

        try
        {
            BlockHeader header = block.Header.Clone();
            BlockExecutionContext txContext = new(header, _blockExecutionContext.Spec);
            transactionProcessor.SetBlockExecutionContext(in txContext);

            bool result = results[txIndex] = transactionProcessor.Execute(transaction, tracer);
            if (result) scope.WorldState.Commit(_blockExecutionContext.Spec, txTracer, commitRoots: true);
            EnsureFeeKeys(env.WorldStateScopeProvider, txIndex);
            feeAccumulator.MarkCommitted(txIndex);
            wroteNewLocation = multiVersionMemory.Record(version, env.WorldStateScopeProvider.ReadSet, env.WorldStateScopeProvider.WriteSet);
            receipts[txIndex] = !result ? null : tracer.LastReceipt;

            return Status.Ok;
        }
        catch (AbortParallelExecutionException e)
        {
            blockingTx = e.BlockingRead.TxIndex;
            return Status.ReadError;
        }
        finally
        {
            txTracer.Dispose();
            tracer.EndTxTrace();
            _tracers.Return(tracer);
        }
    }

    private void EnsureFeeKeys(MultiVersionMemoryScopeProvider scopeProvider, int txIndex)
    {
        Address? gasBeneficiary = feeAccumulator.GasBeneficiary;
        EnsureFeeKey(scopeProvider, FeeRecipientKind.GasBeneficiary, gasBeneficiary, txIndex);

        Address? feeCollector = feeAccumulator.FeeCollector;
        if (feeCollector != gasBeneficiary)
        {
            EnsureFeeKey(scopeProvider, FeeRecipientKind.FeeCollector, feeCollector, txIndex);
        }
    }

    private void EnsureFeeKey(MultiVersionMemoryScopeProvider scopeProvider, FeeRecipientKind kind, Address? recipient, int txIndex)
    {
        if (recipient is not null)
        {
            ParallelStateKey key = ParallelStateKey.ForFee(kind, txIndex);
            if (!scopeProvider.WriteSet.ContainsKey(key))
            {
                scopeProvider.WriteSet[key] = UInt256.Zero;
                feeAccumulator.RecordFee(txIndex, recipient, UInt256.Zero, createAccount: false);
            }
        }
    }
}
