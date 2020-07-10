﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
        int ChainId { get; }
        
        /// <summary>
        /// Genesis block or <value>null</value> if genesis has not been processed yet
        /// </summary>
        BlockHeader Genesis { get; }
        
        /// <summary>
        /// Best header that has been suggested for processing
        /// </summary>
        BlockHeader BestSuggestedHeader { get; }

        /// <summary>
        /// Best block that has been suggested for processing
        /// </summary>
        Block BestSuggestedBody { get; }
        
        /// <summary>
        /// Lowest header added in reverse insert
        /// </summary>
        BlockHeader LowestInsertedHeader { get; }

        /// <summary>
        /// Lowest header added in reverse insert
        /// </summary>
        Block LowestInsertedBody { get; }
        
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

        /// <summary>
        /// Suggests block for inclusion in the block tree.
        /// </summary>
        /// <param name="block">Block to be included</param>
        /// <param name="shouldProcess">Whether a block should be processed or just added to the store</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult SuggestBlock(Block block, bool shouldProcess = true);

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
        /// Marks all <paramref name="processedBlocks"/> as processed, changes chain head to the last of them and updates all the chain levels./>
        /// </summary>
        /// <param name="processedBlocks">Blocks that will now be at the top of the chain</param>
        /// <param name="wereProcessed"></param>
        void UpdateMainChain(Block[] processedBlocks, bool wereProcessed);

        bool CanAcceptNewBlocks { get; }

        Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken);

        ChainLevelInfo FindLevel(long number);

        Keccak FindHash(long blockNumber);

        BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse);

        BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant, long maxSearchDepth);
        
        void DeleteInvalidBlock(Block invalidBlock);

        event EventHandler<BlockEventArgs> NewBestSuggestedBlock;
        event EventHandler<BlockEventArgs> BlockAddedToMain;
        event EventHandler<BlockEventArgs> NewHeadBlock;

        int DeleteChainSlice(in long startNumber, long? endNumber = null);
    }
}