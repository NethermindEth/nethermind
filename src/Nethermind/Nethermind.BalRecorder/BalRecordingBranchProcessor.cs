// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.BalRecorder;

/// <summary>
/// Decorates <see cref="IBranchProcessor"/> to attach replayed block access lists and enable the
/// EIP-7928 spec switch before branch-level work runs.
/// </summary>
/// <remarks>
/// The block cache prewarmer decides whether to perform BAL read-warming inside
/// <see cref="IBranchProcessor.Process"/>, before any <see cref="IBlockProcessor.ProcessOne"/> is
/// reached. Attaching the replayed BAL or flipping the spec switch any later leaves the prewarmer
/// blind to both, so this interception must sit on the branch processor. Recording is left to
/// <see cref="BalRecordingBlockProcessor"/>: the branch processor suppresses its
/// <c>BlockProcessed</c> event for read-only processing (e.g. block production), so recording
/// from here would silently miss those blocks.
/// </remarks>
public class BalRecordingBranchProcessor(
    IBranchProcessor inner,
    IRecordedBalStore store,
    BalRecorderSpecSwitch balSwitch) : IBranchProcessor
{
    public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token = default)
    {
        // Attach replayed BALs before delegating so the prewarmer sees them when it runs.
        if (store.ReplayEnabled)
        {
            foreach (Block block in suggestedBlocks)
            {
                if (!block.IsGenesis && block.BlockAccessList is null)
                    block.BlockAccessList = store.Get(block.Number);
            }
        }

        bool shouldFlip = ShouldFlip(suggestedBlocks);
        if (shouldFlip) balSwitch.Enabled = true;
        try
        {
            return inner.Process(baseBlock, suggestedBlocks, processingOptions, blockTracer, token);
        }
        finally
        {
            if (shouldFlip) balSwitch.Enabled = false;
        }
    }

    private bool ShouldFlip(IReadOnlyList<Block> suggestedBlocks)
    {
        bool recording = store.RecordingEnabled;
        bool replay = store.ReplayEnabled;
        foreach (Block block in suggestedBlocks)
        {
            if (block.IsGenesis) continue;
            if (recording) return true;
            if (replay && block.BlockAccessList is not null) return true;
        }
        return false;
    }

    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed
    {
        add => inner.BlockProcessed += value;
        remove => inner.BlockProcessed -= value;
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing
    {
        add => inner.BlocksProcessing += value;
        remove => inner.BlocksProcessing -= value;
    }

    public event EventHandler<BranchProcessingCompletedEventArgs>? BranchProcessingCompleted
    {
        add => inner.BranchProcessingCompleted += value;
        remove => inner.BranchProcessingCompleted -= value;
    }

    public event EventHandler<BlockEventArgs>? BlockProcessing
    {
        add => inner.BlockProcessing += value;
        remove => inner.BlockProcessing -= value;
    }
}
