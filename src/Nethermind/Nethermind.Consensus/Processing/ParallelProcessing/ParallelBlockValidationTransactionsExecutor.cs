// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
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
        OffParallelTrace trace = OffParallelTrace.Instance;
        MultiVersionMemory multiVersionMemory = new(txCount, trace);
        ParallelScheduler scheduler = new(txCount, trace, _setPool);
        BlockHeader parent = blockFinder.FindParentHeader(block.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        ParallelTransactionProcessor parallelTransactionProcessor = new(block, parent, parallelEnvFactory, multiVersionMemory, preBlockCaches, receipts, in _blockExecutionContext, transactionProcessedEventHandler);
        using ParallelRunner parallelRunner = new(scheduler, multiVersionMemory, trace, parallelTransactionProcessor, 4);
        parallelRunner.Run().GetAwaiter().GetResult();
        PushChanges(stateProvider, multiVersionMemory);
        BlockReceiptsTracer.AccumulateBlockBloom(block, receipts);
        return receipts;
    }

    private void PushChanges(IWorldState worldState, MultiVersionMemory multiVersionMemory)
    {
        Dictionary<StorageCell, object> result = multiVersionMemory.Snapshot();
        foreach (KeyValuePair<StorageCell, object> changes in result)
        {
            switch (changes.Value)
            {
                case Account account: worldState.SetAccount(changes.Key.Address, account); break;
                case byte[] value: worldState.Set(changes.Key, value); break;
                case { } o when o == MultiVersionMemory.SelfDestructMonit: worldState.ClearStorage(changes.Key.Address); break;
            }
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
    in BlockExecutionContext blockExecutionContext,
    ITransactionProcessedEventHandler? transactionProcessedEventHandler) : IParallelTransactionProcessor<StorageCell, object>
{
    private readonly ObjectPool<BlockReceiptsTracer> _tracers = new DefaultObjectPool<BlockReceiptsTracer>(new DefaultPooledObjectPolicy<BlockReceiptsTracer>());
    private readonly BlockExecutionContext _blockExecutionContext = blockExecutionContext;

    public Status TryExecute(Version version, out Version? blockingTx, out HashSet<Read<StorageCell>> readSet, out Dictionary<StorageCell, object> writeSet)
    {
        int txIndex = version.TxIndex;
        blockingTx = null;
        Transaction transaction = block.Transactions[txIndex];

        using IReadOnlyTxProcessingScope scope = CreateProcessingScope(
            out ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv env,
            out ITransactionProcessor transactionProcessor);

        using ITxTracer txTracer = PrepareTracers(out BlockReceiptsTracer tracer);

        try
        {
            TransactionResult result = transactionProcessor.Execute(transaction, tracer);
            if (!result)
            {
                // TODO: What about static checks like nonce?
                InvalidTransactionException.ThrowInvalidTransactionException(result, block.Header, transaction, txIndex);
            }

            scope.WorldState.Commit(_blockExecutionContext.Spec, txTracer, commitRoots: true);
            TxReceipt receipt = receipts[txIndex] = tracer.LastReceipt;
            transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(txIndex, transaction, block.Header, receipt));
            return Status.Ok;
        }
        catch (AbortParallelExecutionException e)
        {
            blockingTx = e.BlockingRead;
            return Status.ReadError;
        }
        finally
        {
            _tracers.Return(tracer);
            readSet = env.WorldStateScopeProvider.ReadSet;
            writeSet = env.WorldStateScopeProvider.WriteSet;
        }

        IReadOnlyTxProcessingScope CreateProcessingScope(
            out ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv environment,
            out ITransactionProcessor txProcessor)
        {
            environment = parallelEnvFactory.Create(version, multiVersionMemory, preBlockCaches);
            IReadOnlyTxProcessingScope s = environment.Build(parentBlock);
            txProcessor = s.TransactionProcessor;
            txProcessor.SetBlockExecutionContext(_blockExecutionContext);
            return s;
        }

        ITxTracer PrepareTracers(out BlockReceiptsTracer blockReceiptsTracer)
        {
            blockReceiptsTracer = _tracers.Get();
            blockReceiptsTracer.StartNewBlockTrace(block);
            return blockReceiptsTracer.StartNewTxTrace(transaction);
        }
    }
}
