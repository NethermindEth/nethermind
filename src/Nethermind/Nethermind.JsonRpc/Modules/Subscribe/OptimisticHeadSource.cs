// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Subscribe;

/// <summary>Announces a block that is likely to become the head before it does.</summary>
public interface IOptimisticHeadSource
{
    /// <summary>
    /// Raised once a block has been validated and committed, when it is the next block on top of the current head and is
    /// not canonical yet: after the merge, the head moves only with the consensus client's forkchoice update.
    /// </summary>
    /// <remarks>Raised on the block-processing thread; handlers must only queue their work.</remarks>
    event EventHandler<BlockEventArgs>? OptimisticHead;
}

/// <summary>
/// Takes the processing queue's successful removals (the block is committed and its state can be queried by hash) and
/// raises <see cref="IOptimisticHeadSource.OptimisticHead"/> for the one that extends the current head.
/// </summary>
/// <remarks>
/// Before the merge the processor moves the head itself before the removal, so nothing is raised there. Blocks older than
/// two slots are not raised either: catching up is no head race, and they are announced when they become canonical.
/// </remarks>
public sealed class OptimisticHeadSource : IOptimisticHeadSource, IDisposable
{
    private readonly IBlockProcessingQueue _processingQueue;
    private readonly IBlockTree _blockTree;
    private readonly ITimestamper _timestamper;
    private readonly ulong _maxAgeSeconds;
    private readonly ILogger _logger;

    [ConstructorWithSideEffect]
    public OptimisticHeadSource(
        IBlockProcessingQueue processingQueue,
        IBlockTree blockTree,
        ITimestamper timestamper,
        ulong secondsPerSlot,
        ILogManager logManager)
    {
        _processingQueue = processingQueue;
        _blockTree = blockTree;
        _timestamper = timestamper;
        _maxAgeSeconds = 2 * Math.Max(secondsPerSlot, 1UL);
        _logger = logManager.GetClassLogger<OptimisticHeadSource>();
        _processingQueue.BlockRemoved += OnBlockRemoved;
    }

    public event EventHandler<BlockEventArgs>? OptimisticHead;

    private void OnBlockRemoved(object? sender, BlockRemovedEventArgs e)
    {
        EventHandler<BlockEventArgs>? handlers = OptimisticHead;
        if (handlers is null || e.ProcessingResult != ProcessingResult.Success) return;

        try
        {
            if (FindCandidate(e.BlockHash) is { } block) handlers(this, new BlockEventArgs(block));
        }
        catch (Exception exception)
        {
            // Never let a subscriber's failure reach the processing loop.
            if (_logger.IsDebug) _logger.Debug($"Optimistic head announcement for {e.BlockHash} failed: {exception}");
        }
    }

    /// <summary>The block named by <paramref name="blockHash"/> when it is the next block on top of the current head.</summary>
    internal Block? FindCandidate(Hash256 blockHash)
    {
        BlockHeader? head = _blockTree.Head?.Header;
        if (head?.Hash is null) return null;

        Block? block = _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (block is null || block.Number != head.Number + 1 || block.ParentHash != head.Hash) return null;

        return block.Timestamp + _maxAgeSeconds < _timestamper.UnixTime.Seconds ? null : block;
    }

    public void Dispose() => _processingQueue.BlockRemoved -= OnBlockRemoved;
}
