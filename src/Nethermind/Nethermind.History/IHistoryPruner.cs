// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.History;

public interface IHistoryPruner
{
    /// <summary>Block number below which historical blocks may be pruned. <c>null</c> when pruning is disabled.</summary>
    public ulong? CutoffBlockNumber { get; }

    /// <summary>Block number below which historical block-access-lists may be pruned. <c>null</c> when pruning is disabled.</summary>
    public ulong? BalCutoffBlockNumber { get; }

    public BlockHeader? OldestBlockHeader { get; }

    /// <summary>Oldest block the reclaim has not physically deleted yet - the published boundary moves
    /// ahead of the reclaim by design, so data between the two is declared absent but still readable.</summary>
    public ulong OldestUnreclaimedBlockNumber { get; }

    event EventHandler<OnNewOldestBlockArgs> NewOldestBlock;

    void SchedulePruneHistory();

    /// <summary>Runs pruning passes back to back until nothing more is owed under the current configuration.</summary>
    /// <remarks>
    /// Ignores <see cref="IHistoryConfig.PruningInterval"/> and <see cref="IHistoryConfig.PruningTimeoutSeconds"/>.
    /// For offline use by the <c>prune-history</c> command.
    /// </remarks>
    /// <exception cref="HistoryPruner.HistoryPrunerException">
    /// Pruning is disabled, the pruning boundary could not be established (no head, no sync pivot, or the ancient
    /// bodies backfill is still descending), or the transaction index sweep stopped making progress.
    /// </exception>
    void PruneToCompletion(CancellationToken cancellationToken);

    /// <summary>
    /// Converts a retention window expressed in epochs to a block count using this pruner's
    /// slots-per-epoch constant. Keeps the epoch→blocks conversion co-located with the pruner.
    /// </summary>
    ulong GetRetentionBlocks(ulong retentionEpochs);
}
