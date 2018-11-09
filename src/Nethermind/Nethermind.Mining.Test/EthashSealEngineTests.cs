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
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining.Difficulty;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mining.Test
{
    [TestFixture]
    public class EthashSealEngineTests
    {
        [Test]
        public async Task Can_mine()
        {
            ulong validNonce = 971086423715459953;

            BlockHeader header = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 1000, 1, 21000, 1, new byte[] {1, 2, 3});
            header.TransactionsRoot = Keccak.Zero;
            header.ReceiptsRoot = Keccak.Zero;
            header.OmmersHash = Keccak.Zero;
            header.StateRoot = Keccak.Zero;
            header.Bloom = Bloom.Empty;

            Block block = new Block(header);
            EthashSealEngine ethashSealEngine = new EthashSealEngine(new Ethash(NullLogManager.Instance), Substitute.For<IDifficultyCalculator>(), NullLogManager.Instance);
            await ethashSealEngine.MineAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token, block, validNonce - 10);

            Assert.AreEqual(validNonce, block.Header.Nonce);
            Assert.AreEqual(new Keccak("0xff2c80283f139148a9b3f2a9dd19d698475937a85296225a96857599cce6d1e2"), block.Header.MixHash);

            Console.WriteLine(block.Header.Nonce);
        }

        [Test]
        public async Task Can_cancel()
        {
            ulong badNonce = 971086423715459953; // change if valid

            BlockHeader header = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, (UInt256)BigInteger.Pow(2, 32), 1, 21000, 1, new byte[] {1, 2, 3});
            header.TransactionsRoot = Keccak.Zero;
            header.ReceiptsRoot = Keccak.Zero;
            header.OmmersHash = Keccak.Zero;
            header.StateRoot = Keccak.Zero;
            header.Bloom = Bloom.Empty;

            Block block = new Block(header);
            EthashSealEngine ethashSealEngine = new EthashSealEngine(new Ethash(NullLogManager.Instance), Substitute.For<IDifficultyCalculator>(), NullLogManager.Instance);
            await ethashSealEngine.MineAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(2000)).Token, block, badNonce).ContinueWith(t =>
            {
                Assert.True(t.IsCanceled);
            });
        }

        [Test]
        [Ignore("use just for finding nonces for other tests")]
        public async Task Find_nonce()
        {
            BlockHeader parentHeader = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131072, 0, 21000, 0, new byte[] { });
            parentHeader.Hash = BlockHeader.CalculateHash(parentHeader);

            BlockHeader blockHeader = new BlockHeader(parentHeader.Hash, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131136, 1, 21000, 1, new byte[] { });
            blockHeader.Nonce = 7217048144105167954;
            blockHeader.MixHash = new Keccak("0x37d9fb46a55e9dbbffc428f3a1be6f191b3f8eaf52f2b6f53c4b9bae62937105");
            blockHeader.Hash = BlockHeader.CalculateHash(blockHeader);
            Block block = new Block(blockHeader);

            IEthash ethash = new Ethash(NullLogManager.Instance);
            EthashSealEngine ethashSealEngine = new EthashSealEngine(ethash, Substitute.For<IDifficultyCalculator>(), NullLogManager.Instance);
            await ethashSealEngine.MineAsync(CancellationToken.None, block, 7217048144105167954);

            Assert.True(ethash.Validate(block.Header));

            Console.WriteLine(block.Header.Nonce);
            Console.WriteLine(block.Header.MixHash);
        }
    }
}