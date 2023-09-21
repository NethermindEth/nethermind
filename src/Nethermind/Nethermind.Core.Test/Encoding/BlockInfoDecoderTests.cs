// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
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
            Rlp rlp = Rlp.Encode((BlockInfo)null!);
            rlp.Length.Should().Be(1);

            BlockInfo decoded = Rlp.Decode<BlockInfo>(rlp);
            decoded.Should().BeNull();
        }

        private static void Roundtrip(bool valueDecode)
        {
            BlockInfo blockInfo = new(TestItem.KeccakA, 1);
            blockInfo.WasProcessed = true;
            blockInfo.IsFinalized = true;
            blockInfo.Metadata |= BlockMetadata.Invalid;

            Rlp rlp = Rlp.Encode(blockInfo);
            BlockInfo decoded = valueDecode ? Rlp.Decode<BlockInfo>(rlp.Bytes.AsSpan()) : Rlp.Decode<BlockInfo>(rlp);

            Assert.True(decoded.WasProcessed, "0 processed");
            Assert.True((decoded.Metadata & BlockMetadata.Finalized) == BlockMetadata.Finalized, "metadata finalized");
            Assert.True((decoded.Metadata & BlockMetadata.Invalid) == BlockMetadata.Invalid, "metadata invalid");
            Assert.That(decoded.BlockHash, Is.EqualTo(TestItem.KeccakA), "block hash");
            Assert.That(decoded.TotalDifficulty, Is.EqualTo(UInt256.One), "difficulty");
        }

        private static void RoundtripBackwardsCompatible(bool valueDecode, bool chainWithFinalization, bool isFinalized)
        {
            BlockInfo blockInfo = new(TestItem.KeccakA, 1);
            blockInfo.WasProcessed = true;
            blockInfo.IsFinalized = isFinalized;

            Rlp rlp = BlockInfoEncodeDeprecated(blockInfo, chainWithFinalization);
            BlockInfo decoded = valueDecode ? Rlp.Decode<BlockInfo>(rlp.Bytes.AsSpan()) : Rlp.Decode<BlockInfo>(rlp);

            Assert.True(decoded.WasProcessed, "0 processed");
            Assert.That(decoded.IsFinalized, Is.EqualTo(chainWithFinalization && isFinalized), "finalized");
            Assert.That(decoded.BlockHash, Is.EqualTo(TestItem.KeccakA), "block hash");
            Assert.That(decoded.TotalDifficulty, Is.EqualTo(UInt256.One), "difficulty");
        }

        public static Rlp BlockInfoEncodeDeprecated(BlockInfo? item, bool chainWithFinalization)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            int contentLength = 0;
            contentLength += Rlp.LengthOf(item.BlockHash);
            contentLength += Rlp.LengthOf(item.WasProcessed);
            contentLength += Rlp.LengthOf(item.TotalDifficulty);
            if (chainWithFinalization)
            {
                contentLength += Rlp.LengthOf(item.IsFinalized);
            }

            RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
            stream.StartSequence(contentLength);
            stream.Encode(item.BlockHash);
            stream.Encode(item.WasProcessed);
            stream.Encode(item.TotalDifficulty);

            if (chainWithFinalization)
            {
                stream.Encode(item.IsFinalized);
            }

            return new Rlp(stream.Data!);
        }
    }
}
