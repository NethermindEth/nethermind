// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

public class BlockStmTransactionsExecutor(
    IBlockProcessor.IBlockTransactionsExecutor inner,
    ITransactionProcessorAdapter transactionProcessorAdapter,
    ParallelEnvFactory parallelEnvFactory,
    IBlockFinder blockFinder,
    IWorldState stateProvider,
    IBlocksConfig blocksConfig,
    ILogManager logManager,
    BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ObjectPool<HashSet<int>> _setPool = new DefaultObjectPool<HashSet<int>>(new DefaultPooledObjectPolicy<HashSet<int>>());

    // Pools for per-tx aggregate collections used in PushChanges. Large maximumRetained so a
    // single high-tx-count block fully recycles between blocks instead of repeatedly allocating.
    private const int AggregatePoolCapacity = 1024;
    private static readonly ObjectPool<Dictionary<Address, Account?>> AccountUpdatesPool =
        new DefaultObjectPool<Dictionary<Address, Account?>>(new ClearingDictPolicy<Address, Account?>(), AggregatePoolCapacity);
    private static readonly ObjectPool<HashSet<Address>> StorageClearsPool =
        new DefaultObjectPool<HashSet<Address>>(new ClearingHashSetPolicy<Address>(), AggregatePoolCapacity);
    private static readonly ObjectPool<List<(StorageCell Cell, byte[] Value)>> StorageWritesPool =
        new DefaultObjectPool<List<(StorageCell Cell, byte[] Value)>>(new ClearingListPolicy<(StorageCell Cell, byte[] Value)>(), AggregatePoolCapacity);

    private sealed class ClearingDictPolicy<TK, TV> : PooledObjectPolicy<Dictionary<TK, TV>> where TK : notnull
    {
        public override Dictionary<TK, TV> Create() => [];
        public override bool Return(Dictionary<TK, TV> obj) { obj.Clear(); return true; }
    }
    private sealed class ClearingHashSetPolicy<T> : PooledObjectPolicy<HashSet<T>>
    {
        public override HashSet<T> Create() => [];
        public override bool Return(HashSet<T> obj) { obj.Clear(); return true; }
    }
    private sealed class ClearingListPolicy<T> : PooledObjectPolicy<List<T>>
    {
        public override List<T> Create() => [];
        public override bool Return(List<T> obj) { obj.Clear(); return true; }
    }

    private BlockExecutionContext _blockExecutionContext;
    private readonly ILogger _logger = logManager.GetClassLogger<BlockStmTransactionsExecutor>();
    private readonly MvmmSystemWriteCapture _systemWriteCapture = new();

    Evm.Tracing.ITxTracer? IBlockProcessor.IBlockTransactionsExecutor.PreTxStateCommitTracer => _systemWriteCapture;

    /// <summary>
    /// Most recent per-block metrics from this executor instance. Tests can assert against
    /// this without contending on the static <see cref="Metrics"/> counters.
    /// </summary>
    public ParallelBlockMetrics LastBlockSnapshot { get; private set; } = ParallelBlockMetrics.Empty;
    // 0 => use logical-processor count, otherwise the configured value.
    private readonly int _concurrencyLevel = blocksConfig.BlockStmConcurrency > 0
        ? blocksConfig.BlockStmConcurrency
        : Math.Max(1, Environment.ProcessorCount);

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
    {
        // Genesis, system txs, and Optimism deposit txs need strict ordering; fall back
        // to the wrapped sequential executor.
        if (block.IsGenesis || ContainsSequentialOnlyTxs(block.Transactions))
        {
            return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);
        }

        Transaction[] transactions = block.Transactions;
        int txCount = transactions.Length;
        TxReceipt[] receipts = new TxReceipt[txCount];
        TransactionResult[] results = new TransactionResult[txCount];
        bool processedSuccessfully = false;
        MultiVersionMemory multiVersionMemory = new(txCount);
        // Seed MVMM with pre-block system-contract writes (EIP-4788, EIP-2935) captured
        // during the system tx's Execute via PreTxStateCommitTracer. Per-tx parallel scopes
        // read through MVMM first, so the overlay shadows the stale view that
        // worldStateManager's read-only trie store would otherwise return for these slots.
        if (!_systemWriteCapture.IsEmpty)
        {
            multiVersionMemory.SeedSystemOverlay(_systemWriteCapture.BuildOverlay(stateProvider));
            _systemWriteCapture.Reset();
        }
        ParallelBlockMetricsCollector blockMetrics = new(txCount);
        FeeAccumulator feeAccumulator = new(txCount, block.Header.GasBeneficiary, _blockExecutionContext.Spec.FeeCollector);
        ParallelScheduler scheduler = new(txCount, _setPool);
        // Block-level concurrent aggregate of every code-write performed by any tx's EVM.
        // Per-tx resettable scopes use a read-only codeDb so their InsertCode is dropped —
        // TrackingCodeDb captures writes here so PushChanges can replay onto the main state.
        // Codehashes are content-addressed so concurrent writes are idempotent.
        ConcurrentDictionary<ValueHash256, byte[]> blockCodeWrites = new();
        ParallelUnbalancedWork.For(1, txCount, i => FindNonceDependencies(i, block, scheduler));
        BlockHeader parent = blockFinder.FindParentHeader(block.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        IReleaseSpec spec = _blockExecutionContext.Spec;
        // One env per worker, reused across every tx that worker handles.
        ConcurrentBag<ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv> envPool = [];
        ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv CreateEnv() =>
            parallelEnvFactory.Create(multiVersionMemory, feeAccumulator, blockCodeWrites, spec);
        for (int i = 0; i < _concurrencyLevel; i++)
        {
            envPool.Add(CreateEnv());
        }
        ParallelTransactionProcessor parallelTransactionProcessor = new(block, parent, envPool, CreateEnv, multiVersionMemory, feeAccumulator, receipts, results, in _blockExecutionContext);
        try
        {
            using ParallelRunner parallelRunner = new(scheduler, multiVersionMemory, parallelTransactionProcessor, blockMetrics, _concurrencyLevel);
            parallelRunner.Run().GetAwaiter().GetResult();
            ThrowIfInvalidResults(block, transactions, results);
            FinalizeGasUsed(block, receipts);
            FeeRecipientWriteInfo feeRecipientWrites = PushChanges(stateProvider, multiVersionMemory, feeAccumulator, spec, txCount, blockCodeWrites);
            ApplyAccumulatedFees(stateProvider, feeAccumulator, spec, feeRecipientWrites);
            RaiseTransactionProcessedEvents(block, transactions, receipts);
            AccumulateBlockBloom(block, receipts);
            processedSuccessfully = true;
            return receipts;
        }
        finally
        {
            while (envPool.TryTake(out ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv? env))
            {
                env.Dispose();
            }
            ParallelBlockMetrics snapshot = blockMetrics.Snapshot();
            LastBlockSnapshot = snapshot;
            Metrics.ReportBlock(snapshot);
            LogParallelBlockReport(block, snapshot, results, processedSuccessfully);
        }
    }

    private static void FindNonceDependencies(int txIndex, Block block, ParallelScheduler scheduler)
    {
        Address? sender = block.Transactions[txIndex].SenderAddress;
        if (sender is null) return;

        // Park this tx on the IMMEDIATE predecessor that touches the sender's nonce. STM
        // re-execution will discover and serialise further predecessors; pre-seeding only
        // the closest one is enough to break the initial scheduling order — adding all
        // predecessors would create a chain and increase contention on the dep set.
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
                // tuple.Authority is populated by RecoverSignatures before processing; null
                // means signature recovery failed and the auth is dropped at exec, creating
                // no nonce dependency here.
                foreach (AuthorizationTuple tuple in prevTx.AuthorizationList)
                {
                    if (tuple.Authority is not null && tuple.Authority == sender)
                    {
                        scheduler.AbortExecution(txIndex, i, false);
                        return;
                    }
                }
            }
        }
    }

    private FeeRecipientWriteInfo PushChanges(IWorldState worldState, MultiVersionMemory multiVersionMemory, FeeAccumulator feeAccumulator, IReleaseSpec spec, int txCount, IReadOnlyDictionary<ValueHash256, byte[]> blockCodeWrites)
    {
        Address? gasBeneficiary = feeAccumulator.GasBeneficiary;
        Address? feeCollector = feeAccumulator.FeeCollector;
        int gasBeneficiaryLastWrite = -1;
        int feeCollectorLastWrite = -1;

        // Categorize each tx's writes into account/clear/storage buckets in parallel. IWorldState
        // is not thread-safe, so application stays sequential below.
        TxWriteAggregate[] aggregates = new TxWriteAggregate[txCount];
        ParallelUnbalancedWork.For(0, txCount, i =>
        {
            CategorizeTxWrites(ref aggregates[i], multiVersionMemory.GetFinalWriteSet(i));
        });

        try
        {
            for (int txIndex = 0; txIndex < txCount; txIndex++)
            {
                ref TxWriteAggregate agg = ref aggregates[txIndex];

                if (agg.StorageClears is { } clears)
                {
                    // Clear marker is emitted on SELFDESTRUCT and pre-EIP-7610 CREATE/CREATE2 collisions; both wipe prior storage.
                    foreach (Address address in clears) worldState.ClearStorage(address);
                }

                if (agg.StorageWrites is { } writes)
                {
                    foreach ((StorageCell Cell, byte[] Value) write in writes) worldState.Set(write.Cell, write.Value);
                }

                if (agg.AccountUpdates is { } accounts)
                {
                    foreach (KeyValuePair<Address, Account?> accountUpdate in accounts)
                    {
                        ApplyAccountUpdate(worldState, accountUpdate.Key, accountUpdate.Value, spec, blockCodeWrites);

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
        finally
        {
            ReturnAggregatesToPool(aggregates);
        }
    }

    private static void CategorizeTxWrites(ref TxWriteAggregate agg, ConcurrentDictionary<ParallelStateKey, MultiVersionMemory.Value> writeSet)
    {
        foreach (KeyValuePair<ParallelStateKey, MultiVersionMemory.Value> write in writeSet)
        {
            if (write.Value.IsEstimate) continue;

            ParallelStateKey key = write.Key;
            object? value = write.Value.Data;

            switch (key.Kind)
            {
                case ParallelStateKeyKind.Account:
                    // null = SELFDESTRUCT/DeleteAccount; non-null = set/create.
                    (agg.AccountUpdates ??= AccountUpdatesPool.Get())[key.Address] = value as Account;
                    break;
                case ParallelStateKeyKind.StorageClear:
                    (agg.StorageClears ??= StorageClearsPool.Get()).Add(key.Address);
                    break;
                case ParallelStateKeyKind.Storage:
                    if (value is byte[] bytes) (agg.StorageWrites ??= StorageWritesPool.Get()).Add((key.StorageCell, bytes));
                    break;
                // Fees flow through FeeAccumulator separately.
                case ParallelStateKeyKind.FeeGasBeneficiary:
                case ParallelStateKeyKind.FeeCollector:
                    break;
            }
        }
    }

    private static void ReturnAggregatesToPool(TxWriteAggregate[] aggregates)
    {
        for (int i = 0; i < aggregates.Length; i++)
        {
            ref TxWriteAggregate agg = ref aggregates[i];
            if (agg.AccountUpdates is { } accounts) AccountUpdatesPool.Return(accounts);
            if (agg.StorageClears is { } clears) StorageClearsPool.Return(clears);
            if (agg.StorageWrites is { } writes) StorageWritesPool.Return(writes);
        }
    }

    private struct TxWriteAggregate
    {
        public Dictionary<Address, Account?>? AccountUpdates;
        public HashSet<Address>? StorageClears;
        public List<(StorageCell Cell, byte[] Value)>? StorageWrites;
    }

    // Replays a captured final Account state onto the main world state using the typed
    // IWorldState primitives. Code bytes captured by TrackingCodeDb during the per-tx run
    // are re-inserted here (the resettable codeDb dropped them); without this the main
    // state's Account.CodeHash would not resolve and the state root would diverge.
    private static void ApplyAccountUpdate(
        IWorldState worldState,
        Address address,
        Account? account,
        IReleaseSpec spec,
        IReadOnlyDictionary<ValueHash256, byte[]> blockCodeWrites)
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

        ref readonly ValueHash256 oldCodeHash = ref worldState.GetCodeHash(address);
        ValueHash256 newCodeHash = account.CodeHash.ValueHash256;
        if (oldCodeHash == newCodeHash) return;

        if (blockCodeWrites.TryGetValue(newCodeHash, out byte[]? code))
        {
            worldState.InsertCode(address, newCodeHash, code, spec);
            return;
        }
        // Fall back to whatever's already in the main code DB (could be a prior block's
        // code referenced by hash). If neither path has the bytes, fail loudly — silently
        // skipping leaves the Account pointing at a hash with no code, which would cause
        // wrong EVM behaviour on a later block.
        byte[]? existing = worldState.GetCode(newCodeHash);
        if (existing is null || existing.Length == 0)
        {
            throw new InvalidOperationException(
                $"Block-STM: account {address} updated to CodeHash {newCodeHash} but code bytes are not in the per-block writes nor the shared codeDb.");
        }
        worldState.InsertCode(address, newCodeHash, existing, spec);
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
        inner.SetBlockExecutionContext(blockExecutionContext);
    }

    private static bool ContainsSequentialOnlyTxs(Transaction[] transactions)
    {
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction tx = transactions[i];
            if (tx.IsSystem() || tx.Type == TxType.DepositTx) return true;
        }
        return false;
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

    // Per-worker tracers each see one tx, so the outer tracer's EndBlockTrace can't
    // build the block bloom on its own — compute it here from the receipts.
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
    ConcurrentBag<ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv> envPool,
    Func<ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv> envFactory,
    MultiVersionMemory multiVersionMemory,
    FeeAccumulator feeAccumulator,
    TxReceipt[] receipts,
    TransactionResult[] results,
    in BlockExecutionContext blockExecutionContext) : IParallelTransactionProcessor
{
    // BlockReceiptsTracer's ctor takes (parallel: true); needs a custom policy.
    private readonly ObjectPool<BlockReceiptsTracer> _tracers = new DefaultObjectPool<BlockReceiptsTracer>(new ParallelBlockReceiptsTracerPolicy());

    private sealed class ParallelBlockReceiptsTracerPolicy : PooledObjectPolicy<BlockReceiptsTracer>
    {
        public override BlockReceiptsTracer Create() => new(parallel: true);
        public override bool Return(BlockReceiptsTracer obj) => true;
    }
    private readonly BlockExecutionContext _blockExecutionContext = blockExecutionContext;

    public Status TryExecute(TxVersion version, out int? blockingTx, out bool writeSetChanged)
    {
        int txIndex = version.TxIndex;
        blockingTx = null;
        writeSetChanged = false;
        Transaction transaction = block.Transactions[txIndex];

        feeAccumulator.ClearFee(txIndex);
        // Pool is sized to concurrency; grow on miss if runner concurrency outpaces it.
        if (!envPool.TryTake(out ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv? env))
        {
            env = envFactory();
        }

        BlockReceiptsTracer tracer = _tracers.Get();
        tracer.StartNewBlockTrace(block);
        ITxTracer txTracer = tracer.StartNewTxTrace(transaction);

        try
        {
            env.SetTxVersion(in version);
            using IReadOnlyTxProcessingScope scope = env.Build(parentBlock);
            ITransactionProcessor transactionProcessor = scope.TransactionProcessor;
            BlockExecutionContext txContext = new(block.Header, _blockExecutionContext.Spec);
            transactionProcessor.SetBlockExecutionContext(in txContext);

            bool result = results[txIndex] = transactionProcessor.Execute(transaction, tracer);
            if (!result)
            {
                // Failed: publish zero-fee keys + MarkCommitted so later coinbase readers don't livelock; readSet still published so prior-tx funding triggers re-validation.
                receipts[txIndex] = null;
                env.WorldStateScopeProvider.WriteSet.Clear();
                EnsureFeeKeys(env.WorldStateScopeProvider, txIndex);
                feeAccumulator.MarkCommitted(txIndex);
                multiVersionMemory.Record(version, env.WorldStateScopeProvider.ReadSet, env.WorldStateScopeProvider.WriteSet);
                return Status.Ok;
            }
            scope.WorldState.Commit(_blockExecutionContext.Spec, txTracer, commitRoots: true);
            EnsureFeeKeys(env.WorldStateScopeProvider, txIndex);
            feeAccumulator.MarkCommitted(txIndex);
            writeSetChanged = multiVersionMemory.Record(version, env.WorldStateScopeProvider.ReadSet, env.WorldStateScopeProvider.WriteSet);
            // Receipt built by a pooled tracer has Index=0; stamp the real index.
            TxReceipt? receipt = tracer.TxReceipts.Length > 0 ? tracer.LastReceipt : null;
            if (receipt is not null) receipt.Index = txIndex;
            receipts[txIndex] = receipt;

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
            envPool.Add(env);
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
