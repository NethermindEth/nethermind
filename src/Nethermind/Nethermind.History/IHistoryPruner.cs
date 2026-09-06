// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    /// <summary>
    /// Converts a retention window expressed in epochs to a block count using this pruner's
    /// slots-per-epoch constant. Keeps the epoch→blocks conversion co-located with the pruner.
    /// </summary>
    ulong GetRetentionBlocks(ulong retentionEpochs);
}
