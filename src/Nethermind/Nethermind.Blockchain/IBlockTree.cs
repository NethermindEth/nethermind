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
using Nethermind.Int256;

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
        
        BlockHeader? BestSuggestedBeaconHeader { get; }

        /// <summary>
        /// Lowest header added in reverse fast sync insert
        /// </summary>
        BlockHeader? LowestInsertedHeader { get; }

        /// <summary>
        /// Lowest body added in reverse fast sync insert
        /// </summary>
        long? LowestInsertedBodyNumber { get; set; }
        
        /// <summary>
        /// Lowest header number added in reverse beacon sync insert
        /// </summary>
        BlockHeader? LowestInsertedBeaconHeader { get; set; }

        /// <summary>
        /// Best downloaded block number (highest number of chain level on the chain)
        /// </summary>
        long BestKnownNumber { get; }
        
        
        long BestKnownBeaconNumber { get; }

        /// <summary>
        /// Inserts a disconnected block header (without body)
        /// </summary>
        /// <param name="header">Header to add</param>
        /// <param name="options"></param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult Insert(BlockHeader header, BlockTreeInsertOptions options = BlockTreeInsertOptions.None);

        /// <summary>
        /// Inserts a disconnected block body (not for processing).
        /// </summary>
        /// <param name="block">Block to add</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult Insert(Block block, bool saveHeader = false, BlockTreeInsertOptions options = BlockTreeInsertOptions.None);

        void Insert(IEnumerable<Block> blocks);

        void UpdateHeadBlock(Keccak blockHash);

        /// <summary>
        /// Suggests block for inclusion in the block tree.
        /// </summary>
        /// <param name="block">Block to be included</param>
        /// <param name="shouldProcess">Whether a block should be processed or just added to the store</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess, bool? setAsMain = null);
        
        /// <summary>
        /// Suggests block for inclusion in the block tree. Wait for DB unlock if needed.
        /// </summary>
        /// <param name="block">Block to be included</param>
        /// <param name="shouldProcess">Whether a block should be processed or just added to the store</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        Task<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess, bool? setAsMain = null);

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
        /// Checks if beacon block was inserted and the block RLP is in the DB
        /// </summary>
        /// <param name="number">Number of the block to check (needed for faster lookup)</param>
        /// <param name="blockHash">Hash of the block to check</param>
        /// <returns><value>True</value> if known, otherwise <value>False</value></returns>
        bool IsKnownBeaconBlock(long number, Keccak blockHash);

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
        
        void MarkChainAsProcessed(Block[] blocks);

        bool CanAcceptNewBlocks { get; }

        Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken);

        UInt256? BackFillTotalDifficulty(long startNumber, long endNumber, long batchSize, UInt256? startingTotalDifficulty = null);

        ChainLevelInfo? FindLevel(long number);

        BlockInfo FindCanonicalBlockInfo(long blockNumber);

        Keccak FindHash(long blockNumber);

        BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse);

        BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant,
            long maxSearchDepth);

        void DeleteInvalidBlock(Block invalidBlock);

        void ForkChoiceUpdated(Keccak? finalizedBlockHash, Keccak? safeBlockBlockHash);

        void LoadLowestInsertedBeaconHeader();

        event EventHandler<BlockEventArgs> NewBestSuggestedBlock;
        event EventHandler<BlockEventArgs> NewSuggestedBlock;
        event EventHandler<BlockReplacementEventArgs> BlockAddedToMain;
        event EventHandler<BlockEventArgs> NewHeadBlock;

        int DeleteChainSlice(in long startNumber, long? endNumber = null);

        bool IsBetterThanHead(BlockHeader? header);
    }
}
