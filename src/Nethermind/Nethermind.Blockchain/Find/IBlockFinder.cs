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
    public interface IBlockFinder
    {
        Keccak HeadHash { get; }

        Keccak GenesisHash { get; }

        Keccak? PendingHash { get; }

        Block? Head { get; }

        Block? FindBlock(Keccak blockHash, BlockTreeLookupOptions options);

        Block? FindBlock(long blockNumber, BlockTreeLookupOptions options);

        BlockHeader? FindHeader(Keccak blockHash, BlockTreeLookupOptions options);

        BlockHeader? FindHeader(long blockNumber, BlockTreeLookupOptions options);

        Keccak? FindBlockHash(long blockNumber);

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
        /// <returns><value>True</value> if part of the canonical chain, otherwise <value>False</value></returns>
        bool IsMainChain(Keccak blockHash);

        public Block? FindBlock(Keccak blockHash) => FindBlock(blockHash, BlockTreeLookupOptions.None);

        public Block? FindBlock(long blockNumber) => FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);

        public Block? FindGenesisBlock() => FindBlock(GenesisHash, BlockTreeLookupOptions.RequireCanonical);

        public Block? FindHeadBlock() => Head;

        public Block? FindEarliestBlock() => FindGenesisBlock();

        public Block? FindLatestBlock() => FindHeadBlock();

        public Block? FindPendingBlock() =>
            PendingHash == null ? null : FindBlock(PendingHash, BlockTreeLookupOptions.None);

        public BlockHeader? FindHeader(Keccak blockHash) => FindHeader(blockHash, BlockTreeLookupOptions.None);

        public BlockHeader? FindHeader(long blockNumber) =>
            FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);

        public BlockHeader FindGenesisHeader() =>
            FindHeader(GenesisHash, BlockTreeLookupOptions.RequireCanonical)
            ?? throw new Exception("Genesis header could not be found");

        public BlockHeader FindEarliestHeader() => FindGenesisHeader();

        public BlockHeader? FindLatestHeader() => Head?.Header;

        public BlockHeader? FindPendingHeader() =>
            PendingHash == null ? null : FindHeader(PendingHash, BlockTreeLookupOptions.None);

        BlockHeader FindBestSuggestedHeader();

        public Block? FindBlock(BlockParameter? blockParameter, bool headLimit = false)
        {
            if (blockParameter == null)
            {
                return FindLatestBlock();
            }

            return blockParameter.Type switch
            {
                BlockParameterType.Pending => FindPendingBlock(),
                BlockParameterType.Latest => FindLatestBlock(),
                BlockParameterType.Earliest => FindEarliestBlock(),
                BlockParameterType.BlockNumber => headLimit && blockParameter.BlockNumber!.Value >= Head.Number
                    ? FindLatestBlock()
                    : FindBlock(blockParameter.BlockNumber!.Value,
                        blockParameter.RequireCanonical
                            ? BlockTreeLookupOptions.RequireCanonical
                            : BlockTreeLookupOptions.None),
                BlockParameterType.BlockHash => FindBlock(blockParameter.BlockHash!,
                    blockParameter.RequireCanonical
                        ? BlockTreeLookupOptions.RequireCanonical
                        : BlockTreeLookupOptions.None),
                _ => throw new ArgumentException($"{nameof(BlockParameterType)} not supported: {blockParameter.Type}")
            };
        }

        public BlockHeader? FindHeader(BlockParameter? blockParameter, bool headLimit = false)
        {
            if (blockParameter == null)
            {
                return FindLatestHeader();
            }

            return blockParameter.Type switch
            {
                BlockParameterType.Pending => FindPendingHeader(),
                BlockParameterType.Latest => FindLatestHeader(),
                BlockParameterType.Earliest => FindEarliestHeader(),
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
    }
}
