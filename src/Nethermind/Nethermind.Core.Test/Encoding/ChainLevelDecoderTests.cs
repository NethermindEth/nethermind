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

using System.Numerics;
using Nethermind.Core.Encoding;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class ChainLevelDecoderTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            BlockInfo blockInfo = new BlockInfo();
            blockInfo.BlockHash = TestItem.KeccakA;
            blockInfo.TotalDifficulty = 1;
            blockInfo.WasProcessed = true;

            BlockInfo blockInfo2 = new BlockInfo();
            blockInfo2.BlockHash = TestItem.KeccakB;
            blockInfo2.TotalDifficulty = 2;
            blockInfo2.WasProcessed = false;

            ChainLevelInfo chainLevelInfo = new ChainLevelInfo(true, new[] {blockInfo, blockInfo2});
            chainLevelInfo.HasBlockOnMainChain = true;

            Rlp rlp = Rlp.Encode(chainLevelInfo);
            ChainLevelInfo decoded = Rlp.Decode<ChainLevelInfo>(rlp);
            Assert.True(decoded.HasBlockOnMainChain, "has block on the main chain");
            Assert.True(decoded.BlockInfos[0].WasProcessed, "0 processed");
            Assert.False(decoded.BlockInfos[1].WasProcessed, "1 not processed");
            Assert.AreEqual(TestItem.KeccakA, decoded.BlockInfos[0].BlockHash, "block hash");
            Assert.AreEqual(UInt256.One, decoded.BlockInfos[0].TotalDifficulty, "difficulty");
        }
        
        [Test]
        public void Can_handle_nulls()
        {
            Rlp rlp = Rlp.Encode((ChainLevelInfo)null);
            ChainLevelInfo decoded = Rlp.Decode<ChainLevelInfo>(rlp);
            Assert.Null(decoded);
        }
    }
}