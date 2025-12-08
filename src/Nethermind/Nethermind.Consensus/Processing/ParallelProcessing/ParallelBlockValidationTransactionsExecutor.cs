// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelBlockValidationTransactionsExecutor(
    ParallelEnvFactory parallelEnvFactory,
    PreBlockCaches preBlockCaches,
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
        OffParallelTrace trace = new();
        MultiVersionMemory multiVersionMemory = new(txCount, trace);
        ParallelScheduler scheduler = new(txCount, trace, _setPool);
        ParallelTransactionProcessor parallelTransactionProcessor = new(block, parallelEnvFactory, multiVersionMemory, preBlockCaches, receipts, in _blockExecutionContext, transactionProcessedEventHandler);
        ParallelRunner parallelRunner = new(scheduler, multiVersionMemory, trace, parallelTransactionProcessor);
        parallelRunner.Run().GetAwaiter().GetResult();

        BlockReceiptsTracer.AccumulateBlockBloom(block, receipts);
        return receipts;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        _blockExecutionContext = blockExecutionContext;
}

public class ParallelTransactionProcessor(
    Block block,
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
        ParallelEnvFactory.ParallelAutoReadOnlyTxProcessingEnv txProcessorSource = parallelEnvFactory.Create(version, multiVersionMemory, preBlockCaches);
        using IReadOnlyTxProcessingScope scope = txProcessorSource.Build(block.Header);
        ITransactionProcessor transactionProcessor = scope.TransactionProcessor;
        transactionProcessor.SetBlockExecutionContext(_blockExecutionContext);
        BlockReceiptsTracer tracer = _tracers.Get();
        tracer.StartNewBlockTrace(block);
        Transaction transaction = block.Transactions[txIndex];
        try
        {
            TransactionResult result = transactionProcessor.Execute(transaction, tracer);
            if (!result)
            {
                // TODO: What about static checks like nonce?
                InvalidTransactionException.ThrowInvalidTransactionException(result, block.Header, transaction, txIndex);
            }

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
            readSet = txProcessorSource.WorldStateScopeProvider.ReadSet;
            writeSet = txProcessorSource.WorldStateScopeProvider.WriteSet;
        }
    }
}
