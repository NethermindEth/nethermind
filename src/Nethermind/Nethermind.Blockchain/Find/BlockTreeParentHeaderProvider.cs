// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Blockchain.Find;

/// <summary>Resolves parent headers from the block tree.</summary>
public sealed class BlockTreeParentHeaderProvider(IBlockTree blockTree) : IParentHeaderProvider
{
    /// <inheritdoc />
    public BlockHeader? FindParentHeader(BlockHeader target) =>
        target.ParentHash is null
            ? null
            : blockTree.FindHeader(target.ParentHash, BlockTreeLookupOptions.None, target.Number - 1);
}
