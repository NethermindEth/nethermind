//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Find
{
    // ReSharper disable once InconsistentNaming
    public static class IBlockFinderExtensions
    {
        public static BlockHeader? FindParentHeader(this IBlockFinder finder, BlockHeader header, BlockTreeLookupOptions options)
        {
            if (header.MaybeParent is null)
            {
                if (header.ParentHash is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot find parent when parent hash is null on block with hash {header.Hash}.");
                }
                
                BlockHeader parent = finder.FindHeader(header.ParentHash, options);
                header.MaybeParent = new WeakReference<BlockHeader>(parent);
                return parent;
            }

            header.MaybeParent.TryGetTarget(out BlockHeader maybeParent);
            if (maybeParent is null)
            {
                if (header.ParentHash is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot find parent when parent hash is null on block with hash {header.Hash}.");
                }
                
                BlockHeader parent = finder.FindHeader(header.ParentHash, options);
                header.MaybeParent.SetTarget(parent);
                return parent;
            }

            if (maybeParent.TotalDifficulty is null && (options & BlockTreeLookupOptions.TotalDifficultyNotNeeded) == 0)
            {
                if (header.ParentHash is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot find parent when parent hash is null on block with hash {header.Hash}.");
                }
                
                BlockHeader? fromDb = finder.FindHeader(header.ParentHash, options);
                maybeParent.TotalDifficulty = fromDb?.TotalDifficulty;
            }
            
            return maybeParent; 
        }

        public static Block? FindParent(this IBlockFinder finder, Block block, BlockTreeLookupOptions options)
        {
            if (block.Header.ParentHash is null)
            {
                throw new InvalidOperationException(
                    $"Cannot find parent when parent hash is null on block with hash {block.Hash}.");
            }
            
            return finder.FindBlock(block.Header.ParentHash, options);
        }

        public static Block? FindParent(this IBlockFinder finder, BlockHeader blockHeader, BlockTreeLookupOptions options)
        {
            if (blockHeader.ParentHash is null)
            {
                throw new InvalidOperationException(
                    $"Cannot find parent when parent hash is null on block with hash {blockHeader.Hash}.");
            }
            
            return finder.FindBlock(blockHeader.ParentHash, options);
        }

        public static Block? RetrieveHeadBlock(this IBlockFinder finder)
        {
            Keccak? headHash = finder.Head?.Hash;
            return headHash is null ? null : finder.FindBlock(headHash, BlockTreeLookupOptions.None);
        }
    }
}
