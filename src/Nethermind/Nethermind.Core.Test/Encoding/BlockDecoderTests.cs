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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class BlockDecoderTests
    {
        private Block[] _scenarios;

        public BlockDecoderTests()
        {
            var transactions = new Transaction[100];
            for (int i = 0; i < 100; i++)
            {
                transactions[i] = Build.A.Transaction.WithData(new byte[] {(byte) i}).WithNonce((UInt256) i).WithValue((UInt256) i).Signed(new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), TestItem.PrivateKeyA, true).TestObject;
            }

            _scenarios = new[]
            {
                Build.A.Block.WithNumber(1).TestObject,
                Build.A.Block.WithNumber(1).WithTransactions(transactions).WithOmmers(Build.A.BlockHeader.TestObject).WithMixHash(Keccak.EmptyTreeHash).TestObject
            };
        }

        [Test]
        public void Can_do_roundtrip_null([Values(true, false)] bool valueDecoder)
        {
            BlockDecoder decoder = new BlockDecoder();
            Rlp result = decoder.Encode(null);
            Block decoded = valueDecoder ? Rlp.Decode<Block>(result.Bytes.AsSpan()) : Rlp.Decode<Block>(result);
            Assert.IsNull(decoded);
        }

        private string regression5644 = "f902cff9025aa05297f2a4a699ba7d038a229a8eb7ab29d0073b37376ff0311f2bd9c608411830a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0fe77dd4ad7c2a3fa4c11868a00e4d728adcdfef8d2e3c13b256b06cbdbb02ec9a00d0abe08c162e4e0891e7a45a8107a98ae44ed47195c2d041fe574de40272df0a0056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000182160c837a1200825208845c54648eb8613078366336393733363936653733366236390000000000000000000000000000f3ec96e458292ccea72a1e53e95f94c28051ab51880b7e03d933f7fa78c9692f635ae55ac3899c9c6999d33c758b5248a05894a3471282333bcd76067c5d391300a00000000000000000000000000000000000000000000000000000000000000000880000000000000000f86ff86d80843b9aca008252089422ea9f6b28db76a7162054c05ed812deb2f519cd8a152d02c7e14af6800000802da0f67424c67d9f91a87b5437db1bdaa05e29bd020ab474b2f67f7be163c9f650dda02f90ab34b44165d776ae04449b15210076d6a72abe2bda2903d4b87f0d1ce541c0";

        [Test]
        public void Can_do_roundtrip_regression([Values(true, false)] bool valueDecoder)
        {
            BlockDecoder decoder = new BlockDecoder();

            byte[] bytes = Bytes.FromHexString(regression5644);
            Rlp.ValueDecoderContext valueDecoderContext = new Rlp.ValueDecoderContext(bytes);
            Block decoded = valueDecoder ? decoder.Decode(ref valueDecoderContext) : decoder.Decode(new RlpStream(bytes));
            Rlp encoded = decoder.Encode(decoded);
            Assert.AreEqual(encoded.Bytes.ToHexString(), encoded.Bytes.ToHexString());
        }

        [Test]
        public void Can_do_roundtrip_scenarios([Values(true, false)] bool valueDecoder)
        {
            BlockDecoder decoder = new BlockDecoder();
            foreach (Block block in _scenarios)
            {
                Rlp encoded = decoder.Encode(block);
                Rlp.ValueDecoderContext valueDecoderContext = new Rlp.ValueDecoderContext(encoded.Bytes);
                Block decoded = valueDecoder ? decoder.Decode(ref valueDecoderContext) : decoder.Decode(new RlpStream(encoded.Bytes));
                Rlp encoded2 = decoder.Encode(decoded);
                Assert.AreEqual(encoded.Bytes.ToHexString(), encoded2.Bytes.ToHexString());
            }
        }

        [Test]
        public void Get_length_null()
        {
            BlockDecoder decoder = new BlockDecoder();
            Assert.AreEqual(1, decoder.GetLength(null, RlpBehaviors.None));
        }
    }
}
