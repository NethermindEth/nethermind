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
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mining.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class EthashSealEngineTests
    {
        [Test]
        public async Task Can_mine()
        {
            ulong validNonce = 971086423715460064;

            BlockHeader header = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 27, 1, 21000, 1, new byte[] {1, 2, 3});
            header.TxRoot = Keccak.Zero;
            header.ReceiptsRoot = Keccak.Zero;
            header.OmmersHash = Keccak.Zero;
            header.StateRoot = Keccak.Zero;
            header.Bloom = Bloom.Empty;

            Block block = new Block(header);
            EthashSealer ethashSealer = new EthashSealer(new Ethash(LimboLogs.Instance), NullSigner.Instance, LimboLogs.Instance);
            using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(600));
            await ethashSealer.MineAsync(cancellationTokenSource.Token, block, validNonce - 3);

            Assert.AreEqual(validNonce, block.Header.Nonce);
            Assert.AreEqual(new Keccak("0x52b96cf62447129c6bd81f835721ee145b948ae3b05ef6eae454cbf69a5bc05d"), block.Header.MixHash);
        }

        [Test]
        public async Task Can_cancel()
        {
            ulong badNonce = 971086423715459953; // change if valid

            BlockHeader header = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, (UInt256)BigInteger.Pow(2, 32), 1, 21000, 1, new byte[] {1, 2, 3});
            header.TxRoot = Keccak.Zero;
            header.ReceiptsRoot = Keccak.Zero;
            header.OmmersHash = Keccak.Zero;
            header.StateRoot = Keccak.Zero;
            header.Bloom = Bloom.Empty;

            Block block = new Block(header);
            EthashSealer ethashSealer = new EthashSealer(new Ethash(LimboLogs.Instance), NullSigner.Instance, LimboLogs.Instance);
            using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
            await ethashSealer.MineAsync(cancellationTokenSource.Token, block, badNonce).ContinueWith(t =>
            {
                Assert.True(t.IsCanceled);
            });
        }

        [Test]
        [Explicit("use just for finding nonces for other tests")]
        public async Task Find_nonce()
        {
            BlockHeader parentHeader = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131072, 0, 21000, 0, new byte[] { });
            parentHeader.Hash = parentHeader.CalculateHash();

            BlockHeader blockHeader = new BlockHeader(parentHeader.Hash, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131136, 1, 21000, 1, new byte[] { });
            blockHeader.Nonce = 7217048144105167954;
            blockHeader.MixHash = new Keccak("0x37d9fb46a55e9dbbffc428f3a1be6f191b3f8eaf52f2b6f53c4b9bae62937105");
            blockHeader.Hash = blockHeader.CalculateHash();
            Block block = new Block(blockHeader);

            IEthash ethash = new Ethash(LimboLogs.Instance);
            EthashSealer ethashSealer = new EthashSealer(ethash, NullSigner.Instance, LimboLogs.Instance);
            await ethashSealer.MineAsync(CancellationToken.None, block, 7217048144105167954);

            Assert.True(ethash.Validate(block.Header));

            Console.WriteLine(block.Header.Nonce);
            Console.WriteLine(block.Header.MixHash);
        }
    }
}
