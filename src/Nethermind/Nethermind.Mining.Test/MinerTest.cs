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
using NUnit.Framework;

namespace Nethermind.Mining.Test
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
    }
}