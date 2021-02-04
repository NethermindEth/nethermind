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

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Processing
{
    internal class BlockRef
    {
        public BlockRef(Block block, ProcessingOptions processingOptions = ProcessingOptions.None)
        {
            Block = block;
            ProcessingOptions = processingOptions;
            IsInDb = false;
            BlockHash = null;
        }

        public BlockRef(Keccak blockHash, ProcessingOptions processingOptions = ProcessingOptions.None)
        {
            Block = null;
            IsInDb = true;
            BlockHash = blockHash;
            ProcessingOptions = processingOptions;
        }

        public bool IsInDb { get; set; }
        public Keccak? BlockHash { get; set; }
        public Block? Block { get; set; }
        public ProcessingOptions ProcessingOptions { get; }
        
        public bool Resolve(IBlockTree blockTree)
        {
            if (IsInDb)
            {
                Block? block = blockTree.FindBlock(BlockHash!, BlockTreeLookupOptions.None);
                if (block is null)
                {
                    return false;
                }

                Block = block;
                BlockHash = null;
                IsInDb = false;
            }

            return true;
        }
    }
}
