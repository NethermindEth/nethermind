// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Find
{
    public interface IBlockFinder
    {
        Hash256 HeadHash { get; }

        Hash256 GenesisHash { get; }

        Hash256? PendingHash { get; }

        Hash256? FinalizedHash { get; }

        Hash256? SafeHash { get; }

        Block? Head { get; }

        Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null);

        Block? FindBlock(ulong blockNumber, BlockTreeLookupOptions options);

        bool HasBlock(ulong blockNumber, Hash256 blockHash);

        /// Find a header. blockNumber is optional, but specifying it can improve performance.
        BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null);

        BlockHeader? FindHeader(ulong blockNumber, BlockTreeLookupOptions options);

        Hash256? FindBlockHash(ulong blockNumber);

        /// <summary>
        /// Checks if the block is currently in the canonical chain
        /// </summary>
        /// <param name="blockHeader">Block header to check</param>
        /// <returns><value>True</value> if part of the canonical chain, otherwise <value>False</value></returns>
        bool IsMainChain(BlockHeader blockHeader);

        /// <summary>
        /// Checks if the block is currently in the canonical chain
        /// </summary>
        /// <param name="blockHash">Hash of the block to check</param>
        /// <param name="throwOnMissingHash">If should throw <exception cref="InvalidOperationException" /> when hash is not found</param>
        /// <returns><value>True</value> if part of the canonical chain, otherwise <value>False</value></returns>
        bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true);

        public Block? FindBlock(Hash256 blockHash, ulong? blockNumber = null) => FindBlock(blockHash, BlockTreeLookupOptions.None, blockNumber);

        public Block? FindBlock(ulong blockNumber) => FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);

        public Block? FindGenesisBlock() => FindBlock(GenesisHash, BlockTreeLookupOptions.RequireCanonical);

        public Block? FindHeadBlock() => Head;

        public Block? FindEarliestBlock() => FindGenesisBlock();

        public Block? FindLatestBlock() => FindHeadBlock();

        public Block? FindPendingBlock() => PendingHash is null ? null : FindBlock(PendingHash, BlockTreeLookupOptions.None);

        public Block? FindFinalizedBlock() => FinalizedHash is null ? null : FindBlock(FinalizedHash, BlockTreeLookupOptions.None);

        public Block? FindSafeBlock() => SafeHash is null ? null : FindBlock(SafeHash, BlockTreeLookupOptions.None);

        public BlockHeader? FindHeader(Hash256 blockHash, ulong? blockNumber = null) => FindHeader(blockHash, BlockTreeLookupOptions.None, blockNumber: blockNumber);

        public BlockHeader? FindHeader(ulong blockNumber) => FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);

        public BlockHeader FindGenesisHeader() => FindHeader(GenesisHash, BlockTreeLookupOptions.RequireCanonical) ?? throw new Exception("Genesis header could not be found");

        public BlockHeader FindEarliestHeader() => FindGenesisHeader();

        public BlockHeader? FindLatestHeader() => Head?.Header;

        public BlockHeader? FindPendingHeader() => PendingHash is null ? null : FindHeader(PendingHash, BlockTreeLookupOptions.None);

        public BlockHeader? FindFinalizedHeader() => FinalizedHash is null ? null : FindHeader(FinalizedHash, BlockTreeLookupOptions.None);

        public BlockHeader? FindSafeHeader() => SafeHash is null ? null : FindHeader(SafeHash, BlockTreeLookupOptions.None);

        BlockHeader FindBestSuggestedHeader();

        public Block? FindBlock(BlockParameter? blockParameter, bool headLimit = false)
        {
            if (blockParameter is null)
            {
                return FindLatestBlock();
            }

            return blockParameter.Type switch
            {
                BlockParameterType.Pending => FindPendingBlock(),
                BlockParameterType.Latest => FindLatestBlock(),
                BlockParameterType.Earliest => FindEarliestBlock(),
                BlockParameterType.Finalized => FindFinalizedBlock(),
                BlockParameterType.Safe => FindSafeBlock(),
                BlockParameterType.BlockNumber => headLimit && blockParameter.BlockNumber!.Value >= Head.Number
                    ? FindLatestBlock()
                    : FindBlock(blockParameter.BlockNumber!.Value,
                        blockParameter.RequireCanonical
                            ? BlockTreeLookupOptions.RequireCanonical
                            : BlockTreeLookupOptions.None),
                BlockParameterType.BlockHash => blockParameter.BlockHash! == HeadHash
                    ? FindLatestBlock()
                    : FindBlock(blockParameter.BlockHash!, blockParameter.RequireCanonical
                        ? BlockTreeLookupOptions.RequireCanonical
                        : BlockTreeLookupOptions.None),
                _ => throw new ArgumentException($"{nameof(BlockParameterType)} not supported: {blockParameter.Type}")
            };
        }

        public BlockHeader? FindHeader(BlockParameter? blockParameter, bool headLimit = false)
        {
            if (blockParameter is null)
            {
                return FindLatestHeader();
            }

            return blockParameter.Type switch
            {
                BlockParameterType.Pending => FindPendingHeader(),
                BlockParameterType.Latest => FindLatestHeader(),
                BlockParameterType.Earliest => FindEarliestHeader(),
                BlockParameterType.Finalized => FindFinalizedHeader(),
                BlockParameterType.Safe => FindSafeHeader(),
                BlockParameterType.BlockNumber => headLimit && blockParameter.BlockNumber!.Value >= Head.Number
                    ? FindLatestHeader()
                    : FindHeader(blockParameter.BlockNumber!.Value,
                        blockParameter.RequireCanonical
                            ? BlockTreeLookupOptions.RequireCanonical
                            : BlockTreeLookupOptions.None),
                BlockParameterType.BlockHash => FindHeader(blockParameter.BlockHash!,
                    blockParameter.RequireCanonical
                        ? BlockTreeLookupOptions.RequireCanonical
                        : BlockTreeLookupOptions.None),
                _ => throw new ArgumentException($"{nameof(BlockParameterType)} not supported: {blockParameter.Type}")
            };
        }

        public ulong GetLowestBlock();

        /// <summary>
        /// Highest state persisted
        /// </summary>
        ulong? BestPersistedState { get; set; }
    }
}
