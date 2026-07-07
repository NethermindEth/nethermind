// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Carries completion state for a branch-processing attempt.
/// </summary>
/// <remarks>
/// <see cref="SuggestedBlocks"/> is the attempted branch, not a list of blocks that necessarily
/// processed successfully. If processing failed, only the first <see cref="ProcessedBlocksCount"/>
/// blocks from <see cref="SuggestedBlocks"/> completed before the failure.
/// </remarks>
public class BranchProcessingCompletedEventArgs : EventArgs
{
    public BranchProcessingCompletedEventArgs(IReadOnlyList<Block> suggestedBlocks, int processedBlocksCount, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(suggestedBlocks);
        ArgumentOutOfRangeException.ThrowIfNegative(processedBlocksCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(processedBlocksCount, suggestedBlocks.Count);

        SuggestedBlocks = suggestedBlocks;
        ProcessedBlocksCount = processedBlocksCount;
        Exception = exception;
    }

    /// <summary>
    /// Blocks that were submitted for branch processing.
    /// </summary>
    public IReadOnlyList<Block> SuggestedBlocks { get; }

    /// <summary>
    /// Number of blocks from <see cref="SuggestedBlocks"/> that completed processing.
    /// </summary>
    public int ProcessedBlocksCount { get; }

    /// <summary>
    /// Exception that ended branch processing, or <see langword="null"/> when processing completed successfully.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Returns <see langword="true"/> when branch processing completed without an exception.
    /// </summary>
    public bool Succeeded => Exception is null;
}
