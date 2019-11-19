﻿/*
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

using System.Numerics;
using Nethermind.Core.Encoding;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class BlockInfoDecoderTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            BlockInfo blockInfo = new BlockInfo();
            blockInfo.BlockHash = TestItem.KeccakA;
            blockInfo.TotalDifficulty = 1;
            blockInfo.WasProcessed = true;

            Rlp rlp = Rlp.Encode(blockInfo);
            BlockInfo decoded = Rlp.Decode<BlockInfo>(rlp);
            Assert.True(decoded.WasProcessed, "0 processed");
            Assert.AreEqual(TestItem.KeccakA, decoded.BlockHash, "block hash");
            Assert.AreEqual(UInt256.One, decoded.TotalDifficulty, "difficulty");
        }
        
        [Test]
        public void Can_do_roundtrip_with_finalization()
        {
            Rlp.Decoders[typeof(BlockInfo)] = new BlockInfoDecoder(true);
            Can_do_roundtrip();
        }
        
        [Test]
        public void Can_handle_nulls()
        {
            Rlp rlp = Rlp.Encode((BlockInfo)null);
            BlockInfo decoded = Rlp.Decode<BlockInfo>(rlp);
            Assert.Null(decoded);
        }
    }
}