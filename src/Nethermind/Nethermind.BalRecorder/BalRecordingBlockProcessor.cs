// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.BalRecorder;

/// <summary>
/// Decorates <see cref="IBlockProcessor"/> to record the block access list generated for each
/// processed block.
/// </summary>
/// <remarks>
/// Recording stays at the block processor — the layer that actually produces the BAL — rather
/// than the branch processor: <see cref="IBlockProcessor.ProcessOne"/> runs for every processed
/// block, whereas branch-level <c>BlockProcessed</c> events are raised behind a read-only-chain
/// guard unrelated to recording. BAL attachment and the EIP-7928 spec switch are applied earlier
/// by <see cref="BalRecordingBranchProcessor"/>.
/// </remarks>
public class BalRecordingBlockProcessor(
    IBlockProcessor inner,
    IRecordedBalStore store,
    IBlockAccessListManager balManager) : IBlockProcessor, IBlockProcessingPreparer
{
    public event Action? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public void PrepareForProcessing(Block suggestedBlock, ProcessingOptions options, IReleaseSpec spec)
    {
        balManager.ForceConstructGeneratedBlockAccessList = store.RecordingEnabled;
        if (inner is IBlockProcessingPreparer preparer)
        {
            preparer.PrepareForProcessing(suggestedBlock, options, spec);
        }
    }

    public void ClearPreparedForProcessing()
    {
        if (inner is IBlockProcessingPreparer preparer)
        {
            preparer.ClearPreparedForProcessing();
        }
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        // Force the generated BAL to be built even on the parallel/verify-only fast path so it can be recorded.
        balManager.ForceConstructGeneratedBlockAccessList = store.RecordingEnabled;
        (Block block, TxReceipt[] receipts) = inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        if (store.RecordingEnabled)
            // GeneratedBlockAccessList is fully populated by this point:
            // BlockProcessor calls SetBlockAccessList (which merges per-tx BALs) before returning.
            store.Insert(block, balManager.GeneratedBlockAccessList);
        return (block, receipts);
    }
}
