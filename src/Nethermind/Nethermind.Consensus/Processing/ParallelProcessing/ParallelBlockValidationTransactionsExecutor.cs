// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelBlockValidationTransactionsExecutor(
    ITransactionProcessorAdapter transactionProcessorAdapter,
    ParallelEnvFactory parallelEnvFactory,
    PreBlockCaches preBlockCaches,
    IBlockFinder blockFinder,
    IWorldState stateProvider,
    ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ObjectPool<HashSet<int>> _setPool = new DefaultObjectPool<HashSet<int>>(new DefaultPooledObjectPolicy<HashSet<int>>());
    private BlockExecutionContext _blockExecutionContext;

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
    {
        Transaction[] transactions = block.Transactions;
        int txCount = transactions.Length;
        TxReceipt[] receipts = new TxReceipt[txCount];
        TransactionResult[] results = new TransactionResult[txCount];
        OffParallelTrace trace = OffParallelTrace.Instance;
        MultiVersionMemory multiVersionMemory = new(txCount, trace);
        ParallelScheduler scheduler = new(txCount, trace, _setPool);
        ParallelUnbalancedWork.For(1, txCount, i => FindNonceDependencies(i, block, scheduler));
        BlockHeader parent = blockFinder.FindParentHeader(block.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        ParallelTransactionProcessor parallelTransactionProcessor = new(block, parent, parallelEnvFactory, multiVersionMemory, preBlockCaches, receipts, results, in _blockExecutionContext);
        using ParallelRunner parallelRunner = new(scheduler, multiVersionMemory, trace, parallelTransactionProcessor, 4);
        parallelRunner.Run().GetAwaiter().GetResult();
        ThrowIfInvalidResults(block, transactions, results);
        FinalizeGasUsed(block, receipts);
        PushChanges(stateProvider, multiVersionMemory, txCount);
        RaiseTransactionProcessedEvents(block, transactions, receipts);
        BlockReceiptsTracer.AccumulateBlockBloom(block, receipts);
        return receipts;
    }

    private void FindNonceDependencies(int txIndex, Block block, ParallelScheduler scheduler)
    {
        Address? sender = block.Transactions[txIndex].SenderAddress;
        if (sender is null)
        {
            return;
        }

        for (int i = txIndex - 1; i >= 0; i--)
        {
            Transaction prevTx = block.Transactions[i];
            if (prevTx.SenderAddress == sender)
            {
                scheduler.AbortExecution(txIndex, i, false);
                break;
            }

            if (!prevTx.HasAuthorizationList)
            {
                continue;
            }

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

    private void PushChanges(IWorldState worldState, MultiVersionMemory multiVersionMemory, int txCount)
    {
        HashSet<Address> storageTouched = new();

        for (int txIndex = 0; txIndex < txCount; txIndex++)
        {
            Dictionary<Address, Account?>? accountUpdates = null;
            HashSet<Address>? storageClears = null;
            multiVersionMemory.ForEachWriteSet(txIndex, (cell, value) =>
            {
                if (ReferenceEquals(value, MultiVersionMemory.SelfDestructMonit))
                {
                    (storageClears ??= new HashSet<Address>()).Add(cell.Address);
                    return;
                }

                if (value is Account account)
                {
                    (accountUpdates ??= new Dictionary<Address, Account?>())[cell.Address] = account;
                    return;
                }

                if (value is null)
                {
                    (accountUpdates ??= new Dictionary<Address, Account?>())[cell.Address] = null;
                }
            });

            if (storageClears is not null)
            {
                foreach (Address address in storageClears)
                {
                    bool accountDeleted = accountUpdates?.TryGetValue(address, out Account? account) ?? false && account is null;
                    // Avoid clearing when it's just an empty-base hint; keep prior tx writes intact.
                    if (accountDeleted || !storageTouched.Contains(address))
                    {
                        worldState.ClearStorage(address);
                        storageTouched.Add(address);
                    }
                }
            }

            multiVersionMemory.ForEachWriteSet(txIndex, (cell, value) =>
            {
                if (value is byte[] bytes)
                {
                    worldState.Set(cell, bytes);
                    storageTouched.Add(cell.Address);
                }
            });

            if (accountUpdates is not null)
            {
                foreach (KeyValuePair<Address, Account?> accountUpdate in accountUpdates)
                {
                    worldState.SetAccount(accountUpdate.Key, accountUpdate.Value);
                }
            }
        }
    }

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
        if (transactionProcessedEventHandler is null)
        {
            return;
        }

        for (int i = 0; i < receipts.Length; i++)
        {
            transactionProcessedEventHandler.OnTransactionProcessed(new TxProcessedEventArgs(i, transactions[i], block.Header, receipts[i]));
        }
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        transactionProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
        _blockExecutionContext = blockExecutionContext;
    }
}

public class ParallelTransactionProcessor(
    Block block,
    BlockHeader parentBlock,
    ParallelEnvFactory parallelEnvFactory,
    MultiVersionMemory multiVersionMemory,
    PreBlockCaches preBlockCaches,
    TxReceipt[] receipts,
    TransactionResult[] results,
    in BlockExecutionContext blockExecutionContext) : IParallelTransactionProcessor<StorageCell, object>
{
    private readonly ObjectPool<BlockReceiptsTracer> _tracers = new DefaultObjectPool<BlockReceiptsTracer>(new DefaultPooledObjectPolicy<BlockReceiptsTracer>());
    private readonly BlockExecutionContext _blockExecutionContext = blockExecutionContext;
    private readonly TransactionResult[] _results = results;

    public Status TryExecute(Version version, out int? blockingTx, out bool wroteNewLocation)
    {
        int txIndex = version.TxIndex;
        blockingTx = null;
        wroteNewLocation = false;
        Transaction transaction = block.Transactions[txIndex];

        using ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv env = parallelEnvFactory.Create(version, multiVersionMemory, preBlockCaches);
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

            TransactionResult result = transactionProcessor.Execute(transaction, tracer);
            _results[txIndex] = result;
            if (!result)
            {
                receipts[txIndex] = null;
                wroteNewLocation = multiVersionMemory.Record(version, env.WorldStateScopeProvider.ReadSet, env.WorldStateScopeProvider.WriteSet);
                return Status.Ok;
            }

            scope.WorldState.Commit(_blockExecutionContext.Spec, txTracer, commitRoots: true);
            TxReceipt receipt = receipts[txIndex] = tracer.LastReceipt;
            wroteNewLocation = multiVersionMemory.Record(version, env.WorldStateScopeProvider.ReadSet, env.WorldStateScopeProvider.WriteSet);
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
}
