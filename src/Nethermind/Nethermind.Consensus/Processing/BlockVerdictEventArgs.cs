// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

/// <summary>Raised by <see cref="IBlockProcessingQueue.BlockExecuted"/> once a queued block has its verdict.</summary>
public class BlockVerdictEventArgs(Hash256 blockHash, ProcessingResult processingResult) : BlockHashEventArgs(blockHash, processingResult)
{
    /// <summary>
    /// Set by a subscriber that handed the verdict to a request waiting for it. From then on a failure belongs to the
    /// commit, not to the block; without an answer, nothing has told anyone the block is valid.
    /// </summary>
    public bool Answered { get; set; }
}
