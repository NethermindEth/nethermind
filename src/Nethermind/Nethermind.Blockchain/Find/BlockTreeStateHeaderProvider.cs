// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Extensions;
using Nethermind.Core;

namespace Nethermind.Blockchain.Find;

/// <summary>Resolves parent headers and finalized canonical headers from the block tree.</summary>
public sealed class BlockTreeStateHeaderProvider(IBlockTree blockTree) : IStateHeaderProvider
{
    public ulong FinalizedBlockNumber => blockTree.BestKnownNumber.SaturatingSub(Reorganization.MaxDepth);

    /// <inheritdoc />
    public BlockHeader? FindParentHeader(BlockHeader target) =>
        target.ParentHash is null
            ? null
            : blockTree.FindHeader(target.ParentHash, BlockTreeLookupOptions.None, target.Number - 1);

    /// <inheritdoc />
    public BlockHeader? GetFinalizedHeader(ulong blockNumber)
    {
        if (FinalizedBlockNumber < blockNumber) return null;
        return blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
    }
}
