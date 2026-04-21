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
/// Decorates <see cref="IBlockTransactionsExecutor"/> to enable BAL tracing when recording or
/// replay is active, so that pre-Prague blocks are captured correctly. Tracing is enabled in
/// <see cref="SetBlockExecutionContext"/> — before system transactions
/// (BeaconRoot store, blockhash state changes) run in <see cref="BlockProcessor"/>.
/// On Prague, <see cref="BlockProcessor"/> already enables tracing; this is a no-op there.
/// </summary>
public class BalTracingTransactionsExecutor(
    IBlockTransactionsExecutor inner,
    IWorldState stateProvider,
    IRecordedBalStore store) : IBlockTransactionsExecutor
{
    private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        if (_balBuilder is not null && (store.RecordingEnabled || store.ReplayEnabled))
            _balBuilder.TracingEnabled = true;
        inner.SetBlockExecutionContext(blockExecutionContext);
    }

    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
    {
        if (_balBuilder is not null && block.BlockAccessList is not null)
            _balBuilder.LoadSuggestedBlockAccessList(block.BlockAccessList, block.GasUsed);
        return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);
    }
}
