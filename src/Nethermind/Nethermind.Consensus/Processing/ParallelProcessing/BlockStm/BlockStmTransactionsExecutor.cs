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

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

public class BlockStmTransactionsExecutor(
    ITransactionProcessorAdapter transactionProcessorAdapter,
    ParallelEnvFactory parallelEnvFactory,
    PreBlockCaches preBlockCaches,
    IBlockFinder blockFinder,
    IWorldState stateProvider,
    ILogManager logManager,
    BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ObjectPool<HashSet<int>> _setPool = new DefaultObjectPool<HashSet<int>>(new DefaultPooledObjectPolicy<HashSet<int>>());
    private BlockExecutionContext _blockExecutionContext;
    private readonly ILogger _logger = logManager.GetClassLogger<BlockStmTransactionsExecutor>();

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
            IReleaseSpec spec = _blockExecutionContext.Spec;
            FeeRecipientWriteInfo feeRecipientWrites = PushChanges(stateProvider, multiVersionMemory, feeAccumulator, spec, txCount);
            ApplyAccumulatedFees(stateProvider, feeAccumulator, spec, feeRecipientWrites);
            RaiseTransactionProcessedEvents(block, transactions, receipts);
            AccumulateBlockBloom(block, receipts);
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

    private FeeRecipientWriteInfo PushChanges(IWorldState worldState, MultiVersionMemory multiVersionMemory, FeeAccumulator feeAccumulator, IReleaseSpec spec, int txCount)
    {
        HashSet<Address> storageTouched = [];
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
                object? value = write.Value.Data;

                switch (key.Kind)
                {
                    case ParallelStateKeyKind.Account:
                        // Account writes can be either a non-null Account (set/create) or null
                        // (SELFDESTRUCT / DeleteAccount). Both paths must propagate to
                        // worldState; distinguishing happens below at apply time.
                        (accountUpdates ??= [])[key.Address] = value as Account;
                        break;

                    case ParallelStateKeyKind.StorageClear:
                        (storageClears ??= []).Add(key.Address);
                        break;

                    case ParallelStateKeyKind.Storage:
                        if (value is byte[] bytes)
                        {
                            (storageWrites ??= []).Add((key.StorageCell, bytes));
                        }
                        break;

                    // Fee writes are tracked separately via FeeAccumulator; no direct world-state
                    // mutation here.
                    case ParallelStateKeyKind.FeeGasBeneficiary:
                    case ParallelStateKeyKind.FeeCollector:
                        break;
                }
            }

            if (storageClears is not null)
            {
                foreach (Address address in storageClears)
                {
                    // Only treat the clear as a SELFDESTRUCT when this tx also nulled the
                    // account entry. A clear paired with a non-null account update is a
                    // CREATE-into-existing or revival; force-clearing in that case would wipe
                    // earlier txs' storage writes for the same address (audit bug B-PushChanges).
                    bool accountDeletedToNull = accountUpdates is not null
                        && accountUpdates.TryGetValue(address, out Account? deletedAccount)
                        && deletedAccount is null;
                    if (accountDeletedToNull || !storageTouched.Contains(address))
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
                    ApplyAccountUpdate(worldState, accountUpdate.Key, accountUpdate.Value, spec);

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

    /// <summary>
    /// Translates a final-account-state write (a <see cref="Account"/> snapshot or <c>null</c>
    /// for deletion) into the typed <see cref="IWorldState"/> primitives.
    /// </summary>
    /// <remarks>
    /// The original codex implementation used <c>worldState.SetAccount(addr, account)</c> which
    /// no longer exists on master (replaced by per-field primitives). We replicate the same
    /// final state via <see cref="IWorldState.DeleteAccount"/> for null, and via
    /// <see cref="IWorldState.CreateAccountIfNotExists"/> +
    /// <see cref="IWorldState.AddToBalance"/>/<see cref="IWorldState.SubtractFromBalance"/> +
    /// <see cref="IWorldState.SetNonce"/> for non-null. Code propagation
    /// (<see cref="Account.CodeHash"/>) is NOT yet handled here — code bytes inserted by an
    /// in-tx CREATE live in the per-tx scope's CodeDb which is disposed before this point.
    /// That is documented as an outstanding audit issue in BlockStm/README.md.
    /// </remarks>
    private static void ApplyAccountUpdate(IWorldState worldState, Address address, Account? account, IReleaseSpec spec)
    {
        if (account is null)
        {
            if (worldState.AccountExists(address))
            {
                worldState.DeleteAccount(address);
            }
            return;
        }

        worldState.CreateAccountIfNotExists(address, 0, 0);
        UInt256 oldBalance = worldState.GetBalance(address);
        if (account.Balance > oldBalance)
        {
            worldState.AddToBalance(address, account.Balance - oldBalance, spec, out _);
        }
        else if (account.Balance < oldBalance)
        {
            worldState.SubtractFromBalance(address, oldBalance - account.Balance, spec, out _);
        }
        worldState.SetNonce(address, account.Nonce);
        // TODO: code propagation. Inserting code requires the bytes (not just CodeHash); they
        // live in the per-tx scope's CodeDb. The parallel-scope CodeDb passthrough currently
        // discards them on per-tx scope dispose — fix this by tracking code inserts in the
        // executor's WriteSet or by piggy-backing on the worldState's shared codeDb.
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
                worldState.AddToBalanceAndCreateIfNotExists(gasBeneficiary, delta, spec, out _);
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
                    worldState.AddToBalanceAndCreateIfNotExists(feeCollector, delta, spec, out _);
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
                BlockProcessor.BlockValidationTransactionsExecutor.ThrowInvalidTransactionException(
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
                BlockProcessor.BlockValidationTransactionsExecutor.ThrowInvalidTransactionException(result, block.Header, transactions[i], i);
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

    /// <summary>
    /// Accumulates each receipt's bloom into the block header's bloom.
    /// </summary>
    /// <remarks>
    /// The original codex code called <c>BlockReceiptsTracer.AccumulateBlockBloom</c>, a static
    /// helper that no longer exists on master (logic moved into the tracer's
    /// <c>EndBlockTrace</c>). The outer tracer's <c>EndBlockTrace</c> path doesn't fire under
    /// parallel execution (per-worker tracer instances each see exactly one tx), so we compute
    /// the block bloom here from the receipts the executor produced. Pattern mirrors master's
    /// BAL executor in <c>BlockProcessor.ParallelBlockValidationTransactionsExecutor.CombineReceipts</c>.
    /// </remarks>
    private static void AccumulateBlockBloom(Block block, TxReceipt[] receipts)
    {
        Bloom blockBloom = new();
        foreach (TxReceipt receipt in receipts)
        {
            if (receipt?.Bloom is not null)
            {
                blockBloom.Accumulate(receipt.Bloom);
            }
        }
        block.Header.Bloom = blockBloom;
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
    // BlockReceiptsTracer's ctor takes a bool (parallel: true) — no parameterless ctor — so we
    // need a custom pool policy. `parallel: true` matches master's BAL executor pattern: it
    // disables shared mutable state inside the tracer.
    private readonly ObjectPool<BlockReceiptsTracer> _tracers = new DefaultObjectPool<BlockReceiptsTracer>(new ParallelBlockReceiptsTracerPolicy());

    private sealed class ParallelBlockReceiptsTracerPolicy : PooledObjectPolicy<BlockReceiptsTracer>
    {
        public override BlockReceiptsTracer Create() => new(parallel: true);
        public override bool Return(BlockReceiptsTracer obj) => true;
    }
    private readonly BlockExecutionContext _blockExecutionContext = blockExecutionContext;

    public Status TryExecute(TxVersion version, out int? blockingTx, out bool wroteNewLocation)
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
