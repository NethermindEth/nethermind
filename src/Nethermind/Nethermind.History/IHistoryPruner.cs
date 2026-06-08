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

    event EventHandler<OnNewOldestBlockArgs> NewOldestBlock;

    void SchedulePruneHistory();
}
