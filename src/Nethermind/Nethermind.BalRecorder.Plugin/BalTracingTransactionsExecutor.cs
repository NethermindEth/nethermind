// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using static Nethermind.Consensus.Processing.IBlockProcessor;

namespace Nethermind.BalRecorder;

/// <summary>
/// Decorates <see cref="IBlockTransactionsExecutor"/> to enable BAL tracing
/// just before transactions run, after <see cref="BlockProcessor"/> has already
/// set <see cref="IBlockAccessListBuilder.TracingEnabled"/> based on the spec.
/// If tracing was already enabled by the spec this is a no-op.
/// </summary>
public class BalTracingTransactionsExecutor(
    IBlockTransactionsExecutor inner,
    IWorldState stateProvider) : IBlockTransactionsExecutor
{
    private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        if (_balBuilder is not null && !_balBuilder.TracingEnabled)
        {
            _balBuilder.TracingEnabled = true;
            if (block.BlockAccessList is not null)
                _balBuilder.LoadSuggestedBlockAccessList(block.BlockAccessList, block.GasUsed);
        }
        return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
        inner.SetBlockExecutionContext(blockExecutionContext);
}
