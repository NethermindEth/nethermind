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

    /// <summary>Whether a subscriber handed the verdict to a request waiting for it; see <see cref="BlockVerdictEventArgs.Answered"/>.</summary>
    public bool Answered { get; set; }
}
