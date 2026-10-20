// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.History;

/// <summary>The heights every peer must still serve block access lists for: the chain's minimum BAL retention of the head.</summary>
/// <remarks>
/// One extra epoch, as in the pruner's cutoff, covers the CL window's epoch-aligned start. The sync pivot stands in for
/// the head during fast sync, when the head is still near genesis.
/// </remarks>
public sealed class BlockAccessListServingWindow(
    IBlockTree blockTree,
    IHistoryPruner historyPruner,
    ISpecProvider specProvider,
    ITimestamper timestamper,
    IBlocksConfig blocksConfig)
{
    private readonly ulong _windowBlocks = historyPruner.GetRetentionBlocks(specProvider.GenesisSpec.MinBalRetentionEpochs + 1);
    private readonly ulong _retentionSeconds = historyPruner.GetRetentionBlocks(specProvider.GenesisSpec.MinBalRetentionEpochs) * blocksConfig.SecondsPerSlot;

    /// <summary>Whether the block is inside the retention window of the highest known head.</summary>
    /// <param name="blockNumber">The requested block height.</param>
    /// <param name="peerHeadNumber">
    /// The serving peer's head, which may be ahead during catch-up. It is capped at our best known header, so a peer
    /// cannot move a block out of the window by advertising a far head.
    /// </param>
    public bool Contains(ulong blockNumber, ulong peerHeadNumber = 0)
    {
        ulong peerHead = ulong.Min(peerHeadNumber, blockTree.BestKnownNumber);
        ulong head = ulong.Max(peerHead, ulong.Max(blockTree.Head?.Number ?? 0, blockTree.SyncPivot.BlockNumber));
        return blockNumber >= head.SaturatingSub(_windowBlocks);
    }

    /// <summary>Whether every peer must still serve the block's access list, so an unavailable answer is withholding.</summary>
    /// <param name="header">The requested block.</param>
    /// <param name="peerHeadNumber">The serving peer's head, as in <see cref="Contains"/>.</param>
    /// <remarks>
    /// The retention period is in epochs, and missed slots produce no blocks, so a block inside the height window can be
    /// older than the period on a peer that prunes by time. The block must also be within the period of now, which no
    /// peer's head is ahead of.
    /// </remarks>
    public bool MustServe(BlockHeader header, ulong peerHeadNumber) =>
        Contains(header.Number, peerHeadNumber) && header.Timestamp >= timestamper.UnixTime.Seconds.SaturatingSub(_retentionSeconds);
}
