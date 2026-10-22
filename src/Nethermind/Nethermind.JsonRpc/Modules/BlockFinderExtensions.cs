// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules
{
    public static class BlockFinderExtensions
    {
        public const string HeaderNotFound = "header not found";
        internal const string BlockBodyNotFound = "block body not found";
        public const string BlockRangeInFuture = "requested block range is in the future";

        /// <summary>
        /// Whether either bound of a range is a block number past the head, which eth_getLogs and trace_filter reject as invalid params.
        /// </summary>
        /// <remarks>
        /// Compares against the local processed head even while syncing, as geth, erigon and reth do: blocks beyond it
        /// have no state to serve, and a peer-reported head is untrusted.
        /// </remarks>
        public static bool IsRangeInFuture(this IBlockFinder blockFinder, BlockParameter? fromBlock, BlockParameter? toBlock)
        {
            ulong? headNumber = blockFinder.Head?.Number;
            return headNumber < fromBlock?.BlockNumber || headNumber < toBlock?.BlockNumber;
        }

        public static bool IsBlockPruned(this IBlockFinder blockFinder, BlockParameter blockParameter)
        {
            ulong? requestedBlock = blockParameter.BlockNumber;
            if (requestedBlock is null)
            {
                SearchResult<BlockHeader> headerResult = blockFinder.SearchForHeader(blockParameter);
                if (!headerResult.IsError)
                {
                    requestedBlock = headerResult.Object.Number;
                }
            }
            // The served floor, not the published boundary: during a backfill the two differ, and this check
            // must agree with what the node advertises as earliest over eth/69.
            return requestedBlock < blockFinder.LowestServedBlock;
        }

        /// <param name="unknownBlockErrorCode">The code for a block that is not known, instead of <see cref="ErrorCodes.ResourceNotFound"/>.</param>
        public static SearchResult<BlockHeader> SearchForHeader(this IBlockFinder blockFinder, BlockParameter? blockParameter, bool allowNulls = false, int? unknownBlockErrorCode = null)
        {
            if (blockFinder.Head is null)
            {
                return new SearchResult<BlockHeader>("Incorrect head block", ErrorCodes.InternalError);
            }

            blockParameter ??= BlockParameter.Latest;

            BlockHeader header = blockFinder.FindHeader(blockParameter);
            if (blockParameter.RequireCanonical && header is null && !allowNulls && blockParameter.BlockHash is not null)
            {
                header = blockFinder.FindHeader(blockParameter.BlockHash);
                if (header is not null)
                {
                    return new SearchResult<BlockHeader>($"{blockParameter.BlockHash} block is not canonical", ErrorCodes.InvalidInput);
                }
            }

            return header is null && !allowNulls
                ? new SearchResult<BlockHeader>(HeaderNotFound, unknownBlockErrorCode ?? ErrorCodes.ResourceNotFound)
                : new SearchResult<BlockHeader>(header);
        }

        /// <param name="unknownBlockErrorCode">The code for a block that is not known, instead of <see cref="ErrorCodes.ResourceNotFound"/>.</param>
        /// <remarks>
        /// With <paramref name="unknownBlockErrorCode"/>, a block missing from the first lookup is looked up again through its
        /// header, so a known header without a body is reported as <see cref="BlockBodyNotFound"/> and an unresolvable safe or
        /// finalized tag as unknown. Without it, such a tag is <see cref="ErrorCodes.UnknownBlockError"/>, and a known header
        /// without a body reads as unknown unless a canonical hash selects it.
        /// </remarks>
        public static SearchResult<Block> SearchForBlock(this IBlockFinder blockFinder, BlockParameter? blockParameter, bool allowNulls = false, int? unknownBlockErrorCode = null)
        {
            blockParameter ??= BlockParameter.Latest;

            Block block = blockFinder.FindBlock(blockParameter);
            bool isCanonicalBodyMissing = false;
            if (blockParameter.RequireCanonical && block is null && !allowNulls && blockParameter.BlockHash is not null)
            {
                // A canonical header without a body is pruned or missing history, not a side-chain block.
                BlockHeader? header = blockFinder.FindHeader(blockParameter.BlockHash);
                if (header is not null && !blockFinder.IsMainChain(header))
                {
                    return new SearchResult<Block>($"{blockParameter.BlockHash} block is not canonical", ErrorCodes.InvalidInput);
                }

                isCanonicalBodyMissing = header is not null;
            }

            if (block is null)
            {
                if (unknownBlockErrorCode is null && (blockParameter.Equals(BlockParameter.Finalized) || blockParameter.Equals(BlockParameter.Safe)))
                {
                    return new SearchResult<Block>("Unknown block error", ErrorCodes.UnknownBlockError);
                }

                if (blockFinder.IsBlockPruned(blockParameter))
                {
                    return new SearchResult<Block>(
                        $"{ErrorMessages.PrunedHistoryUnavailable} for block {blockParameter}",
                        ErrorCodes.PrunedHistoryUnavailable);
                }

                if (!allowNulls)
                {
                    return unknownBlockErrorCode is null
                        ? new SearchResult<Block>(isCanonicalBodyMissing ? BlockBodyNotFound : HeaderNotFound, ErrorCodes.ResourceNotFound)
                        : SearchForBlockByHeader(blockFinder, blockParameter, unknownBlockErrorCode.Value);
                }
            }

            return new SearchResult<Block>(block);
        }

        /// <summary>
        /// Finds the selected header and the block with its hash: the block is unknown only when its header is, a known
        /// header without a body is missing history, and a block imported since the first lookup is found.
        /// </summary>
        private static SearchResult<Block> SearchForBlockByHeader(IBlockFinder blockFinder, BlockParameter blockParameter, int unknownBlockErrorCode)
        {
            BlockHeader? header = blockFinder.FindHeader(blockParameter);
            if (header?.Hash is null)
            {
                return new SearchResult<Block>(HeaderNotFound, unknownBlockErrorCode);
            }

            Block? block = blockFinder.FindBlock(header.Hash, BlockTreeLookupOptions.None, header.Number);
            return block is null
                ? new SearchResult<Block>(BlockBodyNotFound, ErrorCodes.ResourceNotFound)
                : new SearchResult<Block>(block);
        }

        /// <summary>
        /// Same result as <see cref="SearchForBlock"/>, but returns the header and decodes no body when one is stored.
        /// </summary>
        internal static SearchResult<BlockHeader> SearchForHeaderWithBody(this IBlockFinder blockFinder, BlockParameter blockParameter, int? unknownBlockErrorCode = null)
        {
            BlockHeader? header = blockFinder.FindHeader(blockParameter);
            if (header?.Hash is not null && blockFinder.HasBlock(header.Number, header.Hash))
            {
                return new SearchResult<BlockHeader>(header);
            }

            SearchResult<Block> blockSearch = SearchForBlock(blockFinder, blockParameter, unknownBlockErrorCode: unknownBlockErrorCode);
            return blockSearch.IsError
                ? new SearchResult<BlockHeader>(blockSearch.Error!, blockSearch.ErrorCode)
                : new SearchResult<BlockHeader>(blockSearch.Object!.Header);
        }

        /// <summary>
        /// Searches the canonical headers of a block range, failing as <see cref="SearchForBlock"/> would for a block whose body is unavailable.
        /// </summary>
        /// <param name="unknownBlockErrorCode">
        /// The code for a block that is not known, as for <see cref="SearchForBlock"/>, and for a block selected by hash that is
        /// not canonical or not processed.
        /// </param>
        public static IEnumerable<SearchResult<BlockHeader>> SearchForHeadersOnMainChain(this IBlockFinder blockFinder, BlockParameter fromBlock, BlockParameter toBlock, int? unknownBlockErrorCode = null)
        {
            // A block hash names one block that must be canonical and executed; failing that, it is unavailable like an unknown one.
            int? hashSelectionErrorCode = toBlock.BlockHash is null ? null : unknownBlockErrorCode;
            // Checked before the body, which a side-chain block may lack.
            if (hashSelectionErrorCode is not null && blockFinder.FindHeader(toBlock) is { } selected && !blockFinder.IsMainChain(selected))
            {
                yield return new SearchResult<BlockHeader>($"{selected.Hash} block is not canonical", hashSelectionErrorCode.Value);
                yield break;
            }

            SearchResult<BlockHeader> startingBlock = SearchForHeaderWithBody(blockFinder, fromBlock, unknownBlockErrorCode);
            if (startingBlock.IsError || startingBlock.Object is null)
                yield return startingBlock;
            else
            {
                SearchResult<BlockHeader> finalBlockHeader = SearchForHeader(blockFinder, toBlock, unknownBlockErrorCode: unknownBlockErrorCode);
                if (finalBlockHeader.IsError || finalBlockHeader.Object is null)
                {
                    yield return new SearchResult<BlockHeader>(finalBlockHeader.Error ?? string.Empty, finalBlockHeader.ErrorCode);
                    yield break;
                }
                bool isFinalBlockOnMainChain = blockFinder.IsMainChain(finalBlockHeader.Object);
                bool isStartingBlockOnMainChain = blockFinder.IsMainChain(startingBlock.Object);
                if (!isFinalBlockOnMainChain || !isStartingBlockOnMainChain)
                {
                    Hash256? notCanonicalBlockHash = isFinalBlockOnMainChain
                        ? startingBlock.Object.Hash
                        : finalBlockHeader.Object.Hash;
                    yield return new SearchResult<BlockHeader>($"{notCanonicalBlockHash} block is not canonical", hashSelectionErrorCode ?? ErrorCodes.InvalidInput);
                }
                // During sync a block past the processed head can be canonical without having been executed or validated.
                else if (finalBlockHeader.Object.Number > blockFinder.Head!.Number)
                {
                    yield return new SearchResult<BlockHeader>($"{finalBlockHeader.Object.Hash} block is not processed", hashSelectionErrorCode ?? ErrorCodes.ResourceUnavailable);
                }
                else
                {
                    yield return startingBlock;
                    ulong startingBlockNumber = startingBlock.Object.Number;
                    ulong finalBlockNumber = finalBlockHeader.Object.Number;
                    if (startingBlockNumber > finalBlockNumber)
                    {
                        yield return new SearchResult<BlockHeader>($"From block number: {startingBlockNumber} is greater than to block number {finalBlockNumber}", ErrorCodes.InvalidParams);
                    }

                    for (ulong i = startingBlock.Object.Number + 1; i <= finalBlockHeader.Object.Number; ++i)
                    {
                        yield return SearchForHeaderWithBody(blockFinder, new BlockParameter(i), unknownBlockErrorCode);
                    }
                }

            }

        }
    }
}
