//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Find
{
    public class BlockFinder : IBlockFinder
    {
        private readonly IBlockTree _blockTree;

        public BlockFinder(IBlockTree blockTree)
        {
            _blockTree = blockTree;
        }
        
        public Block FindBlock(Keccak blockHash) => _blockTree.FindBlock(blockHash, BlockTreeLookupOptions.None);
        
        public Block FindBlock(long blockNumber) => _blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        
        public Block FindGenesisBlock() => _blockTree.FindBlock(_blockTree.Genesis.Hash, BlockTreeLookupOptions.RequireCanonical);
        
        public Block FindHeadBlock() => _blockTree.FindBlock(_blockTree.Head.Hash, BlockTreeLookupOptions.None);
        
        public Block FindEarliestBlock() => FindGenesisBlock();
        
        public Block FindLatestBlock() => FindHeadBlock();
        
        public BlockHeader FindHeader(Keccak blockHash) => _blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
        
        public BlockHeader FindHeader(long blockNumber) => _blockTree.FindHeader(blockNumber, BlockTreeLookupOptions.RequireCanonical);
        
        public BlockHeader FindGenesisHeader() => _blockTree.Genesis;
        
        public BlockHeader FindHeadHeader() => _blockTree.Head;
        
        public BlockHeader FindEarliestHeader() => FindGenesisHeader();
        
        public BlockHeader FindLatestHeader() => FindHeadHeader();

        public Block FindPendingBlock()
        {
            return _blockTree.FindBlock(_blockTree.BestSuggestedHeader?.Hash, BlockTreeLookupOptions.None);
        }
        
        public BlockHeader FindPendingHeader()
        {
            return _blockTree.FindHeader(_blockTree.BestSuggestedHeader?.Hash, BlockTreeLookupOptions.None);
        }
    }
}