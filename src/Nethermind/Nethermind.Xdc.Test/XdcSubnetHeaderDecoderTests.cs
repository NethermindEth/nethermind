// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Xdc.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class XdcSubnetHeaderDecoderTests
    {
        private static (XdcSubnetBlockHeader Header, byte[] Bytes) BuildHeaderAndDefaultEncode(XdcSubnetHeaderDecoder codec, bool forSealing = false)
        {
            XdcSubnetBlockHeaderBuilder builder = Build.A.XdcSubnetBlockHeader();
            XdcSubnetBlockHeader header = builder.TestObject;

            Rlp encoded = codec.Encode(header, forSealing ? RlpBehaviors.ForSealing : RlpBehaviors.None);
            return (header, encoded.Bytes);
        }

        [Test]
        public void EncodeDecode_RoundTrip_Matches_AllFields()
        {
            var codec = new XdcSubnetHeaderDecoder();
            var (original, encodedBytes) = BuildHeaderAndDefaultEncode(codec);

            // Decode
            var stream = new RlpStream(encodedBytes);
            BlockHeader? decodedBase = codec.Decode(stream);
            Assert.That(decodedBase, Is.Not.Null, "The decoded header should not be null.");
            Assert.That(decodedBase, Is.InstanceOf<XdcSubnetBlockHeader>(), "The decoded header should be an instance of XdcSubnetBlockHeader.");

            var decoded = (XdcSubnetBlockHeader)decodedBase!;

            // Hash is excluded since decoder sets it from RLP, but original is often not set
            decoded.Should().BeEquivalentTo(original, options => options.Excluding(h => h.Hash));
        }

        [Test]
        public void TotalLength_Equals_GetLength()
        {
            var codec = new XdcSubnetHeaderDecoder();
            var (original, encodedBytes) = BuildHeaderAndDefaultEncode(codec);

            // compare to GetLength
            int expectedTotal = codec.GetLength(original, RlpBehaviors.None);
            Assert.That(encodedBytes.Length, Is.EqualTo(expectedTotal), "Encoded total length should match GetLength().");
        }

        [Test]
        public void TotalLength_Equals_GetLength_ForSealing()
        {
            var codec = new XdcSubnetHeaderDecoder();
            var (original, encodedBytes) = BuildHeaderAndDefaultEncode(codec, true);

            // compare to GetLength
            int expectedTotal = codec.GetLength(original, RlpBehaviors.ForSealing);
            Assert.That(encodedBytes.Length, Is.EqualTo(expectedTotal), "Encoded total length should match GetLength().");
        }


        [Test]
        public void Encode_ForSealing_Omits_Validator_And_NextValidators()
        {
            var decoder = new XdcSubnetHeaderDecoder();
            var (original, encodedBytes) = BuildHeaderAndDefaultEncode(decoder, true);

            // ForSealing encoding
            XdcSubnetBlockHeader unencoded = (XdcSubnetBlockHeader)decoder.Decode(new RlpStream(encodedBytes), RlpBehaviors.ForSealing)!;

            Assert.That(unencoded.Validator, Is.Null, "ForSealing encoding should not contain Validator field.");
            Assert.That(unencoded.NextValidators, Is.Null, "ForSealing encoding should not contain NextValidators field.");
        }

    }
}
