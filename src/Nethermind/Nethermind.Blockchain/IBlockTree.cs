/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain
{
    public interface IBlockTree
    {
        int ChainId { get; }
        BlockHeader Genesis { get; }
        BlockHeader BestSuggested { get; }
        UInt256 BestKnownNumber { get; }
        BlockHeader Head { get; }

        bool CanAcceptNewBlocks { get; }
        Task LoadBlocksFromDb(CancellationToken cancellationToken, UInt256? startBlockNumber, int batchSize = BlockTree.DbLoadBatchSize, int maxBlocksToLoad = int.MaxValue);
        AddBlockResult SuggestBlock(Block block);
        Block FindBlock(Keccak blockHash, bool mainChainOnly);
        BlockHeader FindHeader(Keccak blockHash);
        BlockHeader FindHeader(UInt256 blockNumber);
        Block[] FindBlocks(Keccak blockHash, int numberOfBlocks, int skip, bool reverse);
        Block FindBlock(UInt256 blockNumber);
        bool IsMainChain(Keccak blockHash);
        bool IsKnownBlock(Keccak blockHash);
        void MoveToMain(Block block);
        void MoveToMain(Keccak blockHash);
        void MoveToBranch(Keccak blockHash);
        bool WasProcessed(Keccak blockHash);
        void MarkAsProcessed(Keccak blockHash);

        event EventHandler<BlockEventArgs> NewBestSuggestedBlock;
        event EventHandler<BlockEventArgs> BlockAddedToMain;
        event EventHandler<BlockEventArgs> NewHeadBlock;
    }
}