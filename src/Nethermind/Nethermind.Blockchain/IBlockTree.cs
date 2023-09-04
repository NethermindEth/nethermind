// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        /// Network ID that identifies the chain among the public and private chains (different IDs for mainnet, ETH classic, etc.)
        /// </summary>
        ulong NetworkId { get; }

        /// <summary>
        /// Additional identifier of the chain to mitigate risks described in 155
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
        Block? BestSuggestedBody { get; }

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
        /// Lowest header number added in reverse beacon sync insert. Used to determine if BeaconHeaderSync is completed.
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
        /// <param name="headerOptions"></param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult Insert(BlockHeader header, BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.None);

        /// <summary>
        /// Inserts a disconnected block body (not for processing).
        /// </summary>
        /// <param name="block">Block to add</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult Insert(Block block, BlockTreeInsertBlockOptions insertBlockOptions = BlockTreeInsertBlockOptions.None,
            BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.None);

        void UpdateHeadBlock(Keccak blockHash);

        /// <summary>
        /// Suggests block for inclusion in the block tree.
        /// </summary>
        /// <param name="block">Block to be included</param>
        /// <param name="options">Options for suggesting block, whether a block should be processed or just added to the store.</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess);

        /// <summary>
        /// Suggests block for inclusion in the block tree. Wait for DB unlock if needed.
        /// </summary>
        /// <param name="block">Block to be included</param>
        /// <param name="options">Options for suggesting block, whether a block should be processed or just added to the store.</param>
        /// <returns>Result of the operation, eg. Added, AlreadyKnown, etc.</returns>
        ValueTask<AddBlockResult> SuggestBlockAsync(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess);

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
        void UpdateMainChain(IReadOnlyList<Block> blocks, bool wereProcessed, bool forceHeadBlock = false);

        void MarkChainAsProcessed(IReadOnlyList<Block> blocks);

        bool CanAcceptNewBlocks { get; }

        Task Accept(IBlockTreeVisitor blockTreeVisitor, CancellationToken cancellationToken);

        (BlockInfo? Info, ChainLevelInfo? Level) GetInfo(long number, Keccak blockHash);

        ChainLevelInfo? FindLevel(long number);

        BlockInfo FindCanonicalBlockInfo(long blockNumber);

        Keccak FindHash(long blockNumber);

        BlockHeader[] FindHeaders(Keccak hash, int numberOfBlocks, int skip, bool reverse);

        BlockHeader FindLowestCommonAncestor(BlockHeader firstDescendant, BlockHeader secondDescendant, long maxSearchDepth);

        void DeleteInvalidBlock(Block invalidBlock);

        void ForkChoiceUpdated(Keccak? finalizedBlockHash, Keccak? safeBlockBlockHash);

        event EventHandler<BlockEventArgs> NewBestSuggestedBlock;
        event EventHandler<BlockEventArgs> NewSuggestedBlock;

        /// <summary>
        /// A block is marked as canon
        /// </summary>
        event EventHandler<BlockReplacementEventArgs> BlockAddedToMain;

        /// <summary>
        /// A block is now set as head
        /// </summary>
        event EventHandler<BlockEventArgs> NewHeadBlock;

        /// <summary>
        /// A branch is now set as canon. This is different from `BlockAddedToMain` as it is fired only once for the
        /// the whole branch.
        /// </summary>
        event EventHandler<OnUpdateMainChainArgs> OnUpdateMainChain;

        int DeleteChainSlice(in long startNumber, long? endNumber = null);

        bool IsBetterThanHead(BlockHeader? header);

        void UpdateBeaconMainChain(BlockInfo[]? blockInfos, long clearBeaconMainChainStartPoint);

        void RecalculateTreeLevels();
    }
}
