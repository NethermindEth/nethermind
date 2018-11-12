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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Clique.Test
{
    [TestFixture]
    public class BlockHeaderTests
    {
        [Test]
        public void CliqueHashHeader()
        {
            BlockHeader header = BuildCliqueBlock();

            Keccak expectedHeaderHash = new Keccak("0x7b27b6add9e8d0184c722dde86a2a3f626630264bae3d62ffeea1585ce6e3cdd");
            Keccak headerHash = header.HashCliqueHeader();
            Assert.AreEqual(expectedHeaderHash, headerHash);
        }

        private static BlockHeader BuildCliqueBlock()
        {
            BlockHeader header = Build.A.BlockHeader
                .WithParentHash(new Keccak("0x6d31ab6b6ee360d075bb032a094fb4ea52617268b760d15b47aa439604583453"))
                .WithOmmersHash(Keccak.OfAnEmptySequenceRlp)
                .WithBeneficiary(Address.Zero)
                .WithBloom(Bloom.Empty)
                .WithStateRoot(new Keccak("0x9853b6c62bd454466f4843b73e2f0bdd655a4e754c259d6cc0ad4e580d788f43"))
                .WithTransactionsRoot(PatriciaTree.EmptyTreeHash)
                .WithReceiptsRoot(PatriciaTree.EmptyTreeHash)
                .WithDifficulty(2)
                .WithNumber(269)
                .WithGasLimit(4712388)
                .WithGasUsed(0)
                .WithTimestamp(1492014479)
                .WithExtraData(Bytes.FromHexString("0xd783010600846765746887676f312e372e33856c696e757800000000000000004e2b663c52c4c1ef0db29649f1f4addd93257f33d6fe0ae6bd365e63ac9aac4169e2b761aa245fabbf0302055f01b8b5391fa0a134bab19710fd225ffac3afdf01"))
                .WithMixHash(Keccak.Zero)
                .WithNonce(0UL)
                .TestObject;
            return header;
        }
    }
}