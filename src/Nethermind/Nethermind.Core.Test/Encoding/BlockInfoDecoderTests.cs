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
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class BlockInfoDecoderTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void Can_do_roundtrip(bool valueDecode)
        {
            Roundtrip(valueDecode);
        }

        [TestCase(true, true, true)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        [TestCase(true, false, false)]
        [TestCase(false, true, true)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, false, false)]
        public void Is_Backwards_compatible(bool valueDecode, bool chainWithFinalization, bool isFinalized)
        {
            RoundtripBackwardsCompatible(valueDecode, chainWithFinalization, isFinalized);
        }

        [Test]
        public void Can_handle_nulls()
        {
            Rlp rlp = Rlp.Encode((BlockInfo)null);
            BlockInfo decoded = Rlp.Decode<BlockInfo>(rlp);
            Assert.Null(decoded);
        }
        
        private static void Roundtrip(bool valueDecode)
        {
            BlockInfo blockInfo = new(TestItem.KeccakA, 1);
            blockInfo.WasProcessed = true;

            Rlp rlp = Rlp.Encode(blockInfo);
            BlockInfo decoded = valueDecode ?  Rlp.Decode<BlockInfo>(rlp.Bytes.AsSpan()) : Rlp.Decode<BlockInfo>(rlp);
            
            Assert.True(decoded.WasProcessed, "0 processed");
            Assert.AreEqual(TestItem.KeccakA, decoded.BlockHash, "block hash");
            Assert.AreEqual(UInt256.One, decoded.TotalDifficulty, "difficulty");
        }

        private static void RoundtripBackwardsCompatible(bool valueDecode, bool chainWithFinalization, bool isFinalized)
        {
            BlockInfo blockInfo = new(TestItem.KeccakA, 1);
            blockInfo.WasProcessed = true;
            blockInfo.IsFinalized = isFinalized;

            Rlp rlp = BlockInfoEncodeDeprecated(blockInfo, chainWithFinalization);
            BlockInfo decoded = valueDecode ? Rlp.Decode<BlockInfo>(rlp.Bytes.AsSpan()) : Rlp.Decode<BlockInfo>(rlp);

            Assert.True(decoded.WasProcessed, "0 processed");
            Assert.AreEqual(chainWithFinalization && isFinalized, decoded.IsFinalized, "finalized");
            Assert.AreEqual(TestItem.KeccakA, decoded.BlockHash, "block hash");
            Assert.AreEqual(UInt256.One, decoded.TotalDifficulty, "difficulty");
        }

        public static Rlp BlockInfoEncodeDeprecated(BlockInfo? item, bool chainWithFinalization)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }

            Rlp[] elements = new Rlp[chainWithFinalization ? 4 : 3];
            elements[0] = Rlp.Encode(item.BlockHash);
            elements[1] = Rlp.Encode(item.WasProcessed);
            elements[2] = Rlp.Encode(item.TotalDifficulty);

            if (chainWithFinalization)
            {
                elements[3] = Rlp.Encode(item.IsFinalized);
            }

            return Rlp.Encode(elements);
        }
    }
}
