// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;

namespace Nethermind.Eez.Execution.Stateless;

/// <summary>
/// Records the state root at each selected position of a block: before its first transaction, once the pre-execution
/// system calls are committed, and after each selected transaction. Post-execution changes are never included.
/// </summary>
/// <param name="positions">Ascending transaction indices, led by <see cref="EezTransactionCheckpoint.PreExecution"/> when selected.</param>
internal sealed class TransactionCheckpointRecorder(ISpecProvider specProvider, int[] positions)
    : BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler
{
    private int _next;

    public IWorldState? WorldState { get; set; }

    public Hash256[] StateRoots { get; } = new Hash256[positions.Length];

    /// <summary>Wraps the block's transactions executor, so the empty prefix is recorded before the first transaction.</summary>
    public IBlockProcessor.IBlockTransactionsExecutor Wrap(IBlockProcessor.IBlockTransactionsExecutor inner) => new PreExecutionRecorder(inner, this);

    public void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs) => Record(txProcessedEventArgs.Index, txProcessedEventArgs.BlockHeader);

    private void Record(int position, BlockHeader header)
    {
        if (_next == positions.Length || positions[_next] != position)
        {
            return;
        }

        IWorldState worldState = WorldState!;
        worldState.Commit(specProvider.GetSpec(header), commitRoots: true);
        worldState.RecalculateStateRoot();
        StateRoots[_next++] = worldState.StateRoot;
    }

    private sealed class PreExecutionRecorder(IBlockProcessor.IBlockTransactionsExecutor inner, TransactionCheckpointRecorder recorder)
        : IBlockProcessor.IBlockTransactionsExecutor
    {
        public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
        {
            recorder.Record(EezTransactionCheckpoint.PreExecution, block.Header);
            return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) => inner.SetBlockExecutionContext(in blockExecutionContext);

        public void PublishTransactionProcessedEvents() => inner.PublishTransactionProcessedEvents();

        public void ClearTransactionProcessedEvents() => inner.ClearTransactionProcessedEvents();

        public void SetupTxTimingMetrics(Block block) => inner.SetupTxTimingMetrics(block);

        public long StartTxTimer() => inner.StartTxTimer();

        public void StopTxTimer(int i, long txStart) => inner.StopTxTimer(i, txStart);
    }
}
