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
using System.Collections.Generic;
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

            HeaderDecoder decoder = new();
            Rlp rlp = decoder.Encode(header);
            Rlp.ValueDecoderContext decoderContext = new(rlp.Bytes);
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

            HeaderDecoder decoder = new();
            Rlp rlp = decoder.Encode(header);
            rlp.Bytes[2]++;
            string bytesWithAAA = rlp.Bytes.ToHexString();
            bytesWithAAA = bytesWithAAA.Replace("820aaa", "83000aaa");
            
            rlp = new Rlp(Bytes.FromHexString(bytesWithAAA));

            Rlp.ValueDecoderContext decoderContext = new(rlp.Bytes);
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

            HeaderDecoder decoder = new();
            Rlp rlp = decoder.Encode(header);
            Rlp.ValueDecoderContext decoderContext = new(rlp.Bytes);
            BlockHeader decoded = decoder.Decode(ref decoderContext);
            decoded.Hash = decoded.CalculateHash();

            Assert.AreEqual(header.Hash, decoded.Hash, "hash");
        }

        [Test]
        public void Get_length_null()
        {
            HeaderDecoder decoder = new();
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
        
        [Test]
        public void Can_encode_decode_with_verlkle_proof()
        {
            try
            {
                byte[] verkleProof =
                {
                    121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243,
                    229, 224, 183, 72, 25, 6, 8, 210, 159, 31, 3
                };
                byte[] key =  {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                };

                byte[] value =  {
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2
                };
                List<byte[][]> verkleWitness = new();
                verkleWitness.Add(new[]{key, value});
                HeaderDecoder.Eip1559TransitionBlock = 0;
                HeaderDecoder.VerkleTreeTransitionBlock = 0;
                BlockHeader header = Build.A.BlockHeader.WithBaseFee(123).WithVerkleWitness(verkleProof, verkleWitness).TestObject;
                Rlp rlp = Rlp.Encode(header);
                BlockHeader blockHeader = Rlp.Decode<BlockHeader>(rlp);
                blockHeader.BaseFeePerGas.Should().Be(123);
                blockHeader.VerkleProof.Should().BeEquivalentTo(verkleProof);
                blockHeader.VerkleWitnesses[0][0].Should().BeEquivalentTo(key);
                blockHeader.VerkleWitnesses[0][1].Should().BeEquivalentTo(value);
            }
            finally
            {
                HeaderDecoder.Eip1559TransitionBlock = long.MaxValue;
                HeaderDecoder.VerkleTreeTransitionBlock = long.MaxValue;
            }
        }
    }
}
