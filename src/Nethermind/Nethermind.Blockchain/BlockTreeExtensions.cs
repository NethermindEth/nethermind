// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public static class BlockTreeExtensions
    {
        public static ReadOnlyBlockTree AsReadOnly(this IBlockTree blockTree) => new(blockTree);

        public static BlockHeader? GetProducedBlockParent(this IBlockTree blockTree, BlockHeader? parentHeader) => parentHeader ?? blockTree.Head?.Header;
    }
}
