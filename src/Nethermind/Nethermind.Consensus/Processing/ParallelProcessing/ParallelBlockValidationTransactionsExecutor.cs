// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelBlockValidationTransactionsExecutor() : IBlockProcessor.IBlockTransactionsExecutor
{
    private readonly ObjectPool<HashSet<int>> _pool = new DefaultObjectPool<HashSet<int>>(new DefaultPooledObjectPolicy<HashSet<int>>());
    private BlockExecutionContext _blockExecutionContext;

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
    {
        ParallelTrace<NotTracing> trace = ParallelTrace.Empty;
        int txCount = block.Transactions.Length;

        ParallelRunner<StorageCell, object, NotTracing> runner = new(
                new ParallelScheduler<NotTracing>(txCount, trace, _pool),
                new MultiVersionMemory<StorageCell, object, NotTracing>(txCount, trace),
                trace,
                new ParallelVm());

        runner.Run().GetAwaiter().GetResult();

        for (int i = 0; i < txCount; i++)
        {
            Transaction tx = block.Transactions[i];
            TransactionProcessed?.Invoke(this, new TxProcessedEventArgs(i, tx, receiptsTracer.TxReceipts[i]));
        }

        return receiptsTracer.TxReceipts.ToArray();
    }

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _blockExecutionContext = blockExecutionContext;
    }
}

public class ParallelVm : IVm<StorageCell, object>
{
    public Status TryExecute(int txIndex, out Version? blockingTx, out HashSet<Read<StorageCell>> readSet, out Dictionary<StorageCell, object> writeSet)
    {
        throw new NotImplementedException();
    }
}

public class ParallelSingleBlockProcessingCache : ISingleBlockProcessingCache<AddressAsKey, Account>
{

    public Account? GetOrAdd(AddressAsKey key, Func<AddressAsKey, Account> valueFactory)
    {
        throw new NotImplementedException();
    }

    public bool TryGetValue(AddressAsKey key, out Account value)
    {
        throw new NotImplementedException();
    }

    public Account this[AddressAsKey key]
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    public bool NoResizeClear() => true;
}
