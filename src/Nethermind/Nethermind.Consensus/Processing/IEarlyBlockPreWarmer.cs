// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Starts block cache prewarming early (e.g., at engine_newPayload time) before
/// the block enters the processing queue. The prewarmer task runs in the background
/// and is picked up by <see cref="BranchProcessor"/> when processing begins.
/// </summary>
public interface IEarlyBlockPreWarmer
{
    /// <summary>
    /// Start prewarming for the given block. Returns immediately.
    /// The prewarming runs in the background until cancelled or the block is processed.
    /// </summary>
    void StartEarlyPreWarming(Block block, BlockHeader parentHeader, CancellationToken cancellationToken);

    /// <summary>
    /// Get and consume the early prewarm task for the given block, if one was started.
    /// Returns null if no early prewarming was started for this block.
    /// </summary>
    Task? ConsumeEarlyPreWarmTask(Block block);
}
