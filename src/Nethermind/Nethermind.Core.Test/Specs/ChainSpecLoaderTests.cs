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

using System.Globalization;
using System.IO;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.ChainSpec;
using NUnit.Framework;

namespace Nethermind.Core.Test.Specs
{
    [TestFixture]
    public class ChainSpecLoaderTests
    {
        [Test]
        public void Can_load_ropsten()
        {
            byte[] data = File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/ropsten.json"));
            ChainSpecLoader chainSpecLoader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            ChainSpec chainSpec = chainSpecLoader.Load(data);
            Assert.AreEqual(3, chainSpec.ChainId, $"{nameof(chainSpec.ChainId)}");
            Assert.AreEqual("Ropsten", chainSpec.Name, $"{nameof(chainSpec.Name)}");
            Assert.NotNull(chainSpec.Genesis, $"{nameof(ChainSpec.Genesis)}");

            Assert.AreEqual(0x0000000000000042UL, chainSpec.Genesis.Header.Nonce, $"genesis {nameof(BlockHeader.Nonce)}");
            Assert.AreEqual(Keccak.Zero, chainSpec.Genesis.Header.MixHash, $"genesis {nameof(BlockHeader.MixHash)}");
            Assert.AreEqual(0x100000L, (long)chainSpec.Genesis.Header.Difficulty, $"genesis {nameof(BlockHeader.Difficulty)}");
            Assert.AreEqual(0x100000L, (long)chainSpec.Genesis.Header.Difficulty, $"genesis {nameof(BlockHeader.Difficulty)}");
            Assert.AreEqual(Address.Zero, chainSpec.Genesis.Header.Beneficiary, $"genesis {nameof(BlockHeader.Beneficiary)}");
            Assert.AreEqual(0x00L, (long)chainSpec.Genesis.Header.Timestamp, $"genesis {nameof(BlockHeader.Timestamp)}");
            Assert.AreEqual(Keccak.Zero, chainSpec.Genesis.Header.ParentHash, $"genesis {nameof(BlockHeader.ParentHash)}");
            Assert.AreEqual(
                (byte[])new Hex("0x3535353535353535353535353535353535353535353535353535353535353535"),
                chainSpec.Genesis.Header.ExtraData,
                $"genesis {nameof(BlockHeader.ExtraData)}");
            Assert.AreEqual(0x1000000L, chainSpec.Genesis.Header.GasLimit, $"genesis {nameof(BlockHeader.GasLimit)}");
            
            Assert.NotNull(chainSpec.Allocations, $"{nameof(ChainSpec.Allocations)}");
            Assert.AreEqual(257, chainSpec.Allocations.Count, $"allocations count");
            Assert.AreEqual(
                BigInteger.Zero,
                chainSpec.Allocations[new Address("0000000000000000000000000000000000000018")],
                "account 0000000000000000000000000000000000000018");
            Assert.AreEqual(
                BigInteger.One,
                chainSpec.Allocations[new Address("0000000000000000000000000000000000000001")],
                "account 0000000000000000000000000000000000000001");
            
            Assert.AreEqual(
                BigInteger.Parse("1000000000000000000000000000000"),
                chainSpec.Allocations[new Address("874b54a8bd152966d63f706bae1ffeb0411921e5")],
                "account 874b54a8bd152966d63f706bae1ffeb0411921e5");
        }
    }
}