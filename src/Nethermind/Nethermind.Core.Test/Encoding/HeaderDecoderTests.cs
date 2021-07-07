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
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class HeaderDecoderTests
    {
        [Test]
        public void Can_decode()
        {
            BlockHeader header = Build.A.BlockHeader
                .WithMixHash(Keccak.Compute("mix_hash"))
                .WithNonce(1000)
                .TestObject;

            HeaderDecoder decoder = new HeaderDecoder();
            Rlp rlp = decoder.Encode(header);
            var decoderContext = new Rlp.ValueDecoderContext(rlp.Bytes);
            BlockHeader decoded = decoder.Decode(ref decoderContext);
            decoded.Hash = decoded.CalculateHash();

            Assert.AreEqual(header.Hash, decoded.Hash, "hash");
        }

        [Test]
        public void Can_decode_tricky()
        {
            BlockHeader header = Build.A.BlockHeader
                .WithMixHash(Keccak.Compute("mix_hash"))
                .WithTimestamp(2730)
                .WithNonce(1000)
                .TestObject;

            HeaderDecoder decoder = new HeaderDecoder();
            Rlp rlp = decoder.Encode(header);
            rlp.Bytes[2]++;
            string bytesWithAAA = rlp.Bytes.ToHexString();
            bytesWithAAA = bytesWithAAA.Replace("820aaa", "83000aaa");
            
            rlp = new Rlp(Bytes.FromHexString(bytesWithAAA));

            var decoderContext = new Rlp.ValueDecoderContext(rlp.Bytes);
            BlockHeader decoded = decoder.Decode(ref decoderContext);
            decoded.Hash = decoded.CalculateHash();

            Assert.AreEqual(header.Hash, decoded.Hash, "hash");
        }

        [Test]
        public void Can_decode_aura()
        {
            var auRaSignature = new byte[64];
            new Random().NextBytes(auRaSignature);
            BlockHeader header = Build.A.BlockHeader
                .WithAura(100000000, auRaSignature)
                .TestObject;

            HeaderDecoder decoder = new HeaderDecoder();
            Rlp rlp = decoder.Encode(header);
            var decoderContext = new Rlp.ValueDecoderContext(rlp.Bytes);
            BlockHeader decoded = decoder.Decode(ref decoderContext);
            decoded.Hash = decoded.CalculateHash();

            Assert.AreEqual(header.Hash, decoded.Hash, "hash");
        }

        [Test]
        public void Get_length_null()
        {
            HeaderDecoder decoder = new HeaderDecoder();
            Assert.AreEqual(1, decoder.GetLength(null, RlpBehaviors.None));
        }

        [Test]
        public void Can_handle_nulls()
        {
            Rlp rlp = Rlp.Encode((BlockHeader) null);
            BlockHeader decoded = Rlp.Decode<BlockHeader>(rlp);
            Assert.Null(decoded);
        }
        
        [Test]
        public void Can_encode_decode_with_base_fee()
        {
            try
            {
                HeaderDecoder.Eip1559TransitionBlock = 0;
                BlockHeader header = Build.A.BlockHeader.WithBaseFee(123).TestObject;
                Rlp rlp = Rlp.Encode(header);
                BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);
                blockHeader.BaseFeePerGas.Should().Be(123);
            }
            finally
            {
                HeaderDecoder.Eip1559TransitionBlock = long.MaxValue;
            }
        }
    }
}
