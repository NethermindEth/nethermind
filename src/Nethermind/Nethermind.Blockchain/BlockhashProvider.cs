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

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;

namespace Nethermind.Blockchain
{
    public class BlockhashProvider : IBlockhashProvider
    {
        private readonly IBlockTree _chain;

        public BlockhashProvider(IBlockTree chain)
        {
            _chain = chain;
        }

        public Keccak GetBlockhash(BlockHeader currentBlock, UInt256 number)
        {
            //Block block = _chain.FindHeader(blockHash, false);
            
            if (number >= currentBlock.Number || number < currentBlock.Number - 256)
            {
                return null;
            }

            BlockHeader header = _chain.FindHeader(currentBlock.ParentHash);
            for (int i = 0; i < 256; i++)
            {
                if (number == header.Number)
                {
                    return header.Hash;
                }

                header = _chain.FindHeader(header.ParentHash);
                if (_chain.IsMainChain(header.Hash))
                {
                    header = _chain.FindHeader(number);
                }
            }

            return null;
        }
    }
}