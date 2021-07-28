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
using System.IO;
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
        
        [TestCase("0xf904b6f901faa0418edd4a1d61173fe9eddd71b6f566c7a1741d6d921ccfe95092d4e381b4bdc6a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347948888f1f195afa192cfee860698584c030f4c9db1a0a0a17df881c8b957adf59052ba2dc3ab3f4f055efd7e69fb8c9c4f252c51bb8ca04b8c5617e48aa262447119a6dd62209a5273e285c7c2e9d28c94d11294966116a0dcc94785d0897bff881411737e4ed6a48c27d045d9cc42043e35a52f992bca4eb901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000083020000018302421f830242208454c9906942a00000000000000000000000000000000000000000000000000000000000000000880000000000000000f902b5f861808203e882520894bbbf5374fce5edbc8e2a8697c15331677e6ebf0b0a801ca05f7695292d57b510cd9ff967b1e026a37e7e432d0f23572c11f871da913c702ba05ce190270250ed44f27be21ab68135071eb84c2482cdc055ce99a642be718aaaf861018203e882520894bbbf5374fce5edbc8e2a8697c15331677e6ebf0b0a801ca098bd5f0c06f3645ab205bfb00a0fb15d101e879424904ce9891792001b02f043a05dfad5eac9272f278643eb0dabc37d2f246ced31b366fc807ff58b2c2b3c44abf861028203e882520894bbbf5374fce5edbc8e2a8697c15331677e6ebf0b0a801ca0b773c25c3e7f8fd5471f59fa041f7c599a4e879764c7a433c63ba804f4d3d001a05efcfad1a58852863bf290bad55ee3143e0b79133c62ef6d36c6409dd275e7e2f861038203e882520894bbbf5374fce5edbc8e2a8697c15331677e6ebf0b0a801ba06df49ddfbe6e8a26f79b1a1ced3fcf928d8f7fa1e6743b637d7d7e25c3a3aa9da0594a654aea98887af6a1f0cd33b331b39e2b3ddc930e4dca30e566d790cb9962f861048203e882520894bbbf5374fce5edbc8e2a8697c15331677e6ebf0b0a801ca0754071821febfbc080fe0a19629d36db40ca593ad163db05d25433fb15941e33a04f7b8945034d5cec59ea976dd0eb67b0eeca95ec0f55a2e257d08390e2897583f861058203e882520894bbbf5374fce5edbc8e2a8697c15331677e6ebf0b0a801ba08b705377b151c13985aafeee2a67bc8aa840735b43ba6ac8388fc56b5e7b335da0582cd1afbb929740f91fd4a2cae6ecbcd3352bd9d2031309cb2970e80fbebd9af861068203e88255f094aaaf5374fce5edbc8e2a8697c15331677e6ebf0b0a801ba08c9374e6778b212f708643cd8d70508341b957c189a00947c7cc782c70031497a03547ead4f10392538af113a86b9c58758450a4916c2882247880e42f6790f5abc0")]
        public void Get_length_null2(string rlp)
        {
            File.WriteAllBytes("C:\\blocks\\00001.rlp", Bytes.FromHexString(rlp));
            // var blockRlp = new Rlp(fileContent);
            //
            // Rlp.Decode<Block>(blockRlp);
        }
        
        [Test]
        public void Get_length_null()
        {
            BlockDecoder decoder = new BlockDecoder();
            Assert.AreEqual(1, decoder.GetLength(null, RlpBehaviors.None));
        }
    }
}
