// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.History;

/// <summary>The heights every peer must still serve block access lists for: the chain's minimum BAL retention of the head.</summary>
/// <remarks>
/// One extra epoch, as in the pruner's cutoff, covers the CL window's epoch-aligned start. The sync pivot stands in for
/// the head during fast sync, when the head is still near genesis.
/// </remarks>
public sealed class BlockAccessListServingWindow(IBlockTree blockTree, IHistoryPruner historyPruner, ISpecProvider specProvider)
{
    private readonly ulong _windowBlocks = historyPruner.GetRetentionBlocks(specProvider.GenesisSpec.MinBalRetentionEpochs + 1);

    /// <summary>Whether peers must still serve the block access list at this height.</summary>
    public bool Contains(ulong blockNumber)
    {
        ulong head = ulong.Max(blockTree.Head?.Number ?? 0, blockTree.SyncPivot.BlockNumber);
        return blockNumber >= head.SaturatingSub(_windowBlocks);
    }
}
