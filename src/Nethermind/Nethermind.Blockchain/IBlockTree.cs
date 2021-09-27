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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public interface IBlockTree : IBlockFinder
    {
        /// <summary>
        /// Chain ID that identifies the chain among the public and private chains (different IDs for mainnet, ETH classic, etc.)
        /// </summary>
        ulong ChainId { get; }

        /// <summary>
        /// Genesis block or <value>null</value> if genesis has not been processed yet
        /// </summary>
        BlockHeader? Genesis { get; }

        /// <summary>
        /// Best header that has been suggested for processing
        /// </summary>
        BlockHeader? BestSuggestedHeader { get; }

        /// <summary>
        /// Best block that has been suggested for processing
        /// </summary>
        Block BestSuggestedBody { get; }

        /// <summary>
        /// Lowest header added in reverse insert
        /// </summary>
        BlockHeader? LowestInsertedHeader { get; }

        /// <summary>
        /// Lowest body added in reverse insert
        /// </summary>
        long? LowestInsertedBodyNumber { get; set; }

        /// <summary>
        /// Best downloaded block number (highest number of chain level on the chain)
        /// </summary>
        long BestKnownNumber { get; }

        /// <summary>
        /// Inserts a disconnected block header (without body)
        /// </summary>
        /// <param name="header">Header to add</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult Insert(BlockHeader header);

        /// <summary>
        /// Inserts a disconnected block body (not for processing).
        /// </summary>
        /// <param name="block">Block to add</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult Insert(Block block);

        void Insert(IEnumerable<Block> blocks);

        void UpdateHeadBlock(Keccak blockHash);

        /// <summary>
        /// Suggests block for inclusion in the block tree.
        /// </summary>
        /// <param name="block">Block to be included</param>
        /// <param name="shouldProcess">Whether a block should be processed or just added to the store</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult SuggestBlock(Block block, bool shouldProcess = true, bool? setAsMain = null);

        /// <summary>
        /// Suggests a block header (without body)
        /// </summary>
        /// <param name="header">Header to add</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult SuggestHeader(BlockHeader header);

        /// <summary>
        /// Checks if the block was downloaded and the block RLP is in the DB
        /// </summary>
        /// <param name="number">Number of the block to check (needed for faster lookup)</param>
        /// <param name="blockHash">Hash of the block to check</param>
        /// <returns><value>True</value> if known, otherwise <value>False</value></returns>
        bool IsKnownBlock(long number, Keccak blockHash);

        /// <summary>
        /// Checks if the state changes of the block can be found in the state tree.
        /// </summary>
        /// <param name="number">Number of the block to check (needed for faster lookup)</param>
        /// <param name="blockHash">Hash of the block to check</param>
        /// <returns><value>True</value> if processed, otherwise <value>False</value></returns>
        bool WasProcessed(long number, Keccak blockHash);

        /// <summary>
        /// Marks all <paramref name="blocks"/> as processed, changes chain head to the last of them and updates all the chain levels./>
        /// </summary>
        /// <param name="blocks">Blocks that will now be at the top of the chain</param>
        /// <param name="wereProcessed"></param>
        /// <param name="forceHeadBlock">Force updating <seealso cref="IBlockFinder.Head"/> block regardless of <see cref="Block.TotalDifficulty"/></param>
        void UpdateMainChain(Block[] blocks, bool wereProcessed, bool forceHeadBlock = false);

        bool CanAcceptNewBlocks { get; }

        Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken);

        ChainLevelInfo? FindLevel(long number);

        BlockInfo FindCanonicalBlockInfo(long blockNumber);

        Keccak FindHash(long blockNumber);

        BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse);

        BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant,
            long maxSearchDepth);

        void DeleteInvalidBlock(Block invalidBlock);

        event EventHandler<BlockEventArgs> NewBestSuggestedBlock;
        event EventHandler<BlockEventArgs> NewSuggestedBlock;
        event EventHandler<BlockReplacementEventArgs> BlockAddedToMain;
        event EventHandler<BlockEventArgs> NewHeadBlock;
        
        int DeleteChainSlice(in long startNumber, long? endNumber = null);

        bool IsBetterThanHead(BlockHeader? header)
        {
            bool result = false;
            if (header is not null)
            {
                if (header.IsGenesis && Genesis is null)
                {
                    result = true;
                }
                else
                {
                    result = header.TotalDifficulty > (Head?.TotalDifficulty ?? 0)
                             // so above is better and more correct but creates an impression of the node staying behind on stats page
                             // so we are okay to process slightly more
                             // and below is less correct but potentially reporting well
                             // || totalDifficulty >= (_blockTree.Head?.TotalDifficulty ?? 0)
                             // below are some new conditions under test
                             || (header.TotalDifficulty == Head?.TotalDifficulty &&
                                 ((Head?.Hash ?? Keccak.Zero).CompareTo(header.Hash) > 0))
                             || (header.TotalDifficulty == Head?.TotalDifficulty &&
                                 ((Head?.Number ?? 0L).CompareTo(header.Number) > 0));
                }
            }

            return result;
        }
    }
}
