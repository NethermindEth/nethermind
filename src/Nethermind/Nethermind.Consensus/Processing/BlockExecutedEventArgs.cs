// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Raised by <see cref="IBranchProcessor.BlockExecuted"/>. <see cref="Block"/> is the block as it was suggested, which
/// is what the processing queue answers for; it is not the processed block, so nothing execution computed (roots,
/// bloom, receipts) is read from here. Those arrive with <see cref="BlockProcessedEventArgs"/>.
/// </summary>
public class BlockExecutedEventArgs(Block block) : EventArgs
{
    /// <summary>The block as it was suggested, which is what the processing queue answers for.</summary>
    public Block Block { get; } = block;

    /// <summary>
    /// Set by a subscriber that handed the verdict to a caller waiting for it, as the processing queue does for a copy
    /// queued through <see cref="IBlockProcessingQueue.EnqueueAndWaitForVerdictAsync"/>. From then on a failure belongs
    /// to the commit, not to the block; without an answer, nothing has told anyone the block is valid.
    /// </summary>
    public bool Answered { get; set; }
}
