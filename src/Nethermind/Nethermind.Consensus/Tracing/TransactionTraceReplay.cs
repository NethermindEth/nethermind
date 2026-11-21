// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Tracing;

/// <summary>
/// The replay-only steps a transaction trace block processor adds to the block processor of its chain, so that a chain
/// with its own processor replays a transaction prefix with that processor; see <see cref="TransactionTraceBlockProcessor"/>.
/// </summary>
/// <remarks>An unpersisted replay returns no receipts, so a processor using it must not be installed where its caller
/// reads them, such as receipt regeneration.</remarks>
public sealed class TransactionTraceReplay(TransactionTraceExecutor executor, IBlockAccessListManager balManager)
{
    private readonly bool _reportsProcessedTransactions = executor.ReportsProcessedTransactions;

    public enum Finalization
    {
        /// <summary>The traced prefix is complete, so the rest of the block is not finalized.</summary>
        Skip,
        /// <summary>The block is finalized without deriving the commitments that only a persisted block reads.</summary>
        WithoutCommitments,
        /// <summary>The block is finalized as the processor of the chain finalizes it.</summary>
        Full,
    }

    /// <summary>Returns the options to process the block with and sets whether <paramref name="receiptsTracer"/> builds receipts.</summary>
    public ProcessingOptions BeginBlock(IBlockTracer blockTracer, ProcessingOptions options, BlockReceiptsTracer receiptsTracer)
    {
        if (TransactionTraceBoundary.Get(blockTracer, options) is not null && !balManager.ForceConstructGeneratedBlockAccessList)
            options |= ProcessingOptions.ForceSequentialBlockAccessList;

        receiptsTracer.BuildsReceipts = _reportsProcessedTransactions || !TransactionTraceBoundary.IsUnpersistedReplay(options);
        return options;
    }

    /// <summary>Selects how the block is finalized after its transactions, ending the block trace when it is skipped.</summary>
    public Finalization FinalizeBlock(IBlockTracer blockTracer, ProcessingOptions options, BlockReceiptsTracer receiptsTracer)
    {
        if (!balManager.ForceConstructGeneratedBlockAccessList
            && TransactionTraceBoundary.Get(blockTracer, options)?.IsComplete == true)
        {
            receiptsTracer.EndBlockTrace(accumulateBlockBloom: false);
            return Finalization.Skip;
        }

        // Tracers read only execution; IntermediateRootsBlockTracer derives its roots itself.
        return TransactionTraceBoundary.IsUnpersistedReplay(options) ? Finalization.WithoutCommitments : Finalization.Full;
    }
}
