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
    public class ReadOnlyBlockTree : IBlockTree
    {
        private readonly IBlockTree _wrapped;

        public ReadOnlyBlockTree(IBlockTree wrapped)
        {
            _wrapped = wrapped;
        }

        public int ChainId => _wrapped.ChainId;
        public BlockHeader Genesis => _wrapped.Genesis;
        public BlockHeader BestSuggested => _wrapped.BestSuggested;
        public UInt256 BestKnownNumber => _wrapped.BestKnownNumber;
        public BlockHeader Head => _wrapped.Head;
        public bool CanAcceptNewBlocks { get; } = false;

        public Task LoadBlocksFromDb(CancellationToken cancellationToken, UInt256? startBlockNumber, int batchSize = BlockTree.DbLoadBatchSize, int maxBlocksToLoad = Int32.MaxValue)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(LoadBlocksFromDb)} calls");
        }

        public AddBlockResult SuggestBlock(Block block)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(SuggestBlock)} calls");
        }

        public Block FindBlock(Keccak blockHash, bool mainChainOnly)
        {
            return _wrapped.FindBlock(blockHash, mainChainOnly);
        }

        public BlockHeader FindHeader(Keccak blockHash)
        {
            return _wrapped.FindHeader(blockHash);
        }

        public BlockHeader FindHeader(UInt256 blockNumber)
        {
            return _wrapped.FindHeader(blockNumber);
        }

        public Block[] FindBlocks(Keccak blockHash, int numberOfBlocks, int skip, bool reverse)
        {
            return _wrapped.FindBlocks(blockHash, numberOfBlocks, skip, reverse);
        }

        public Block FindBlock(UInt256 blockNumber)
        {
            return _wrapped.FindBlock(blockNumber);
        }

        public bool IsMainChain(Keccak blockHash)
        {
            return _wrapped.IsMainChain(blockHash);
        }

        public bool IsKnownBlock(Keccak blockHash)
        {
            return _wrapped.IsKnownBlock(blockHash);
        }

        public void MoveToMain(Block block)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(MoveToMain)} calls");
        }

        public void MoveToMain(Keccak blockHash)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(MoveToMain)} calls");
        }

        public void MoveToBranch(Keccak blockHash)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(MoveToBranch)} calls");
        }

        public bool WasProcessed(Keccak blockHash)
        {
            return _wrapped.WasProcessed(blockHash);
        }

        public void MarkAsProcessed(Keccak blockHash)
        {
            throw new InvalidOperationException($"{nameof(ReadOnlyBlockTree)} does not expect {nameof(MarkAsProcessed)} calls");
        }

        public event EventHandler<BlockEventArgs> NewBestSuggestedBlock
        {
            add { }
            remove { }
        }

        public event EventHandler<BlockEventArgs> BlockAddedToMain
        {
            add { }
            remove { }
        }
        
        public event EventHandler<BlockEventArgs> NewHeadBlock
        {
            add { }
            remove { }
        }
    }
}