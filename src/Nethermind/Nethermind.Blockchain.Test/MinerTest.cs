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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Mining;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class MinerTest
    {
        [Test]
        public async Task Can_mine()
        {
            ulong validNonce = 971086423715459222;
            
            BlockHeader header = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 1000, 1, 21000, 1, new byte[] {1, 2, 3});
            Block block = new Block(header);
            Miner miner = new Miner(new Ethash());
            await miner.MineAsync(block, validNonce - 10);

            Assert.AreEqual(validNonce, block.Header.Nonce);
            Assert.AreEqual(new Keccak("0xe009999b2544c84ce29841ba4a38c5d7a22056635bc045a8403f83e96d137d59"), block.Header.MixHash);
            
            Console.WriteLine(block.Header.Nonce);
        }
        
        [Test]
        [Explicit]
        public async Task Find_nonce()
        {   
            BlockHeader parentHeader = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131072, 0, 21000, 0, new byte[]{});
            parentHeader.RecomputeHash();
            Block parentBlock = new Block(parentHeader);
            
            BlockHeader blockHeader = new BlockHeader(parentHeader.Hash, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131136, 1, 21000, 1, new byte[]{});
            blockHeader.Nonce = 7217048144105167954;
            blockHeader.MixHash = new Keccak("0x37d9fb46a55e9dbbffc428f3a1be6f191b3f8eaf52f2b6f53c4b9bae62937105");
            blockHeader.RecomputeHash();
            Block block = new Block(blockHeader);

            IEthash ethash = new Ethash();
            Miner miner = new Miner(ethash);
            await miner.MineAsync(block, 7217048144105167954);

            Assert.True(ethash.Validate(block.Header));
            
            Console.WriteLine(block.Header.Nonce);
            Console.WriteLine(block.Header.MixHash);
        }
    }
}