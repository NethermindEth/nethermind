// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.History;

public interface IHistoryPruner
{
    public long? CutoffBlockNumber { get; }
    public long? BalCutoffBlockNumber { get; }
    public BlockHeader? OldestBlockHeader { get; }

    event EventHandler<OnNewOldestBlockArgs> NewOldestBlock;

    void SchedulePruneHistory();

    /// <summary>
    /// Converts a retention window expressed in epochs to a block count using this pruner's
    /// slots-per-epoch constant. Keeps the epoch→blocks conversion co-located with the pruner.
    /// </summary>
    long GetRetentionBlocks(long retentionEpochs);
}
