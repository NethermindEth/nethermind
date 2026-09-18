// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Core;

namespace Nethermind.Blockchain.Find;

/// <summary>
/// Resolves parent headers and finalized canonical headers from the block tree, treating everything
/// <see cref="Reorganization.MaxDepth"/> blocks behind the best known block as finalized.
/// </summary>
/// <remarks>
/// A reorg-depth heuristic only. Post-merge the Merge plugin decorates this with
/// <c>MergeFinalizedStateProvider</c>, which uses the consensus layer's finalized marker and falls back
/// to this provider until one is available.
/// </remarks>
public sealed class ReorgDepthStateHeaderProvider(IBlockTree blockTree) : IStateHeaderProvider
{
    public ulong FinalizedBlockNumber => blockTree.BestKnownNumber.SaturatingSub(Reorganization.MaxDepth);

    /// <inheritdoc />
    public BlockHeader? FindParentHeader(BlockHeader target) =>
        target.ParentHash is null
            ? null
            : blockTree.FindHeader(target.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing, target.Number - 1);

    /// <inheritdoc />
    public BlockHeader? GetFinalizedHeader(ulong blockNumber)
    {
        if (FinalizedBlockNumber < blockNumber) return null;
        return blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
    }
}
