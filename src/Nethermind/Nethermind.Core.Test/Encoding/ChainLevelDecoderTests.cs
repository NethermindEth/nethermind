// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class ChainLevelDecoderTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void Can_do_roundtrip(bool valueDecode)
        {
            BlockInfo blockInfo = new(TestItem.KeccakA, 1);
            blockInfo.WasProcessed = true;

            BlockInfo blockInfo2 = new(TestItem.KeccakB, 2);
            blockInfo2.WasProcessed = false;

            ChainLevelInfo chainLevelInfo = new(true, new[] { blockInfo, blockInfo2 });
            chainLevelInfo.HasBlockOnMainChain = true;

            Rlp rlp = Rlp.Encode(chainLevelInfo);

            ChainLevelInfo decoded = valueDecode ? Rlp.Decode<ChainLevelInfo>(rlp.Bytes.AsSpan()) : Rlp.Decode<ChainLevelInfo>(rlp);

            Assert.True(decoded.HasBlockOnMainChain, "has block on the main chain");
            Assert.True(decoded.BlockInfos[0].WasProcessed, "0 processed");
            Assert.False(decoded.BlockInfos[1].WasProcessed, "1 not processed");
            Assert.AreEqual(TestItem.KeccakA, decoded.BlockInfos[0].BlockHash, "block hash");
            Assert.AreEqual(UInt256.One, decoded.BlockInfos[0].TotalDifficulty, "difficulty");
        }

        [Test]
        public void Can_handle_nulls()
        {
            Rlp rlp = Rlp.Encode((ChainLevelInfo)null!);
            ChainLevelInfo decoded = Rlp.Decode<ChainLevelInfo>(rlp);
            Assert.Null(decoded);
        }
    }
}
