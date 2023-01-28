// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
