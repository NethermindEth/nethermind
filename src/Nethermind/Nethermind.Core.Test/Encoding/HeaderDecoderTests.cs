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
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using NUnit.Framework.Internal;

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
            BlockHeader decoded = decoder.Decode(new Rlp.ValueDecoderContext(rlp.Bytes));
            decoded.Hash = BlockHeader.CalculateHash(decoded);
            
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
            BlockHeader decoded = decoder.Decode(new Rlp.ValueDecoderContext(rlp.Bytes));
            decoded.Hash = BlockHeader.CalculateHash(decoded);
            
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
            Rlp rlp = Rlp.Encode((BlockHeader)null);
            BlockHeader decoded = Rlp.Decode<BlockHeader>(rlp);
            Assert.Null(decoded);
        }
    }
}