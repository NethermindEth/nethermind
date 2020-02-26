//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain.Find
{
    // ReSharper disable once InconsistentNaming
    public static class IBlockFinderExtensions
    {
        public static BlockHeader FindParentHeader(this IBlockFinder finder, BlockHeader header, BlockTreeLookupOptions options)
        {
            if (header.MaybeParent == null)
            {
                BlockHeader parent = finder.FindHeader(header.ParentHash, options);
                header.MaybeParent = new WeakReference<BlockHeader>(parent);
                return parent;
            }

            header.MaybeParent.TryGetTarget(out BlockHeader maybeParent);
            if (maybeParent == null)
            {
                BlockHeader parent = finder.FindHeader(header.ParentHash, options);
                header.MaybeParent.SetTarget(parent);
                return parent;
            }

            if (maybeParent.TotalDifficulty == null && (options & BlockTreeLookupOptions.TotalDifficultyNotNeeded) == 0)
            {
                BlockHeader fromDb = finder.FindHeader(header.ParentHash, options);
                maybeParent.TotalDifficulty = fromDb.TotalDifficulty;
            }
            
            return maybeParent; 
        }

        public static Block FindParent(this IBlockFinder finder, Block block, BlockTreeLookupOptions options)
        {
            return finder.FindBlock(block.Header.ParentHash, options);
        }

        public static Block FindParent(this IBlockFinder finder, BlockHeader blockHeader, BlockTreeLookupOptions options)
        {
            return finder.FindBlock(blockHeader.ParentHash, options);
        }

        public static Block RetrieveHeadBlock(this IBlockFinder finder)
        {
            return finder.FindBlock(finder.Head.Hash, BlockTreeLookupOptions.None);
        }
    }
}