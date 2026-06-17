// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Nethermind.Xdc.RLP;

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
            XdcSubnetHeaderDecoder codec = new();
            (XdcSubnetBlockHeader? original, byte[]? encodedBytes) = BuildHeaderAndDefaultEncode(codec);

            // Decode
            Rlp.ValueDecoderContext context = encodedBytes.AsRlpValueContext();
            BlockHeader? decodedBase = codec.Decode(ref context);
            Assert.That(decodedBase, Is.Not.Null, "The decoded header should not be null.");
            Assert.That(decodedBase, Is.InstanceOf<XdcSubnetBlockHeader>(), "The decoded header should be an instance of XdcSubnetBlockHeader.");

            XdcSubnetBlockHeader decoded = (XdcSubnetBlockHeader)decodedBase!;

            Assert.That(decoded, Is.EqualTo(original).UsingXdcComparer(compareHash: false));
        }

        [Test]
        public void TotalLength_Equals_GetLength()
        {
            XdcSubnetHeaderDecoder codec = new();
            (XdcSubnetBlockHeader? original, byte[]? encodedBytes) = BuildHeaderAndDefaultEncode(codec);

            // compare to GetLength
            int expectedTotal = codec.GetLength(original, RlpBehaviors.None);
            Assert.That(encodedBytes.Length, Is.EqualTo(expectedTotal), "Encoded total length should match GetLength().");
        }

        [Test]
        public void TotalLength_Equals_GetLength_ForSealing()
        {
            XdcSubnetHeaderDecoder codec = new();
            (XdcSubnetBlockHeader? original, byte[]? encodedBytes) = BuildHeaderAndDefaultEncode(codec, true);

            // compare to GetLength
            int expectedTotal = codec.GetLength(original, RlpBehaviors.ForSealing);
            Assert.That(encodedBytes.Length, Is.EqualTo(expectedTotal), "Encoded total length should match GetLength().");
        }


        [Test]
        public void Encode_ForSealing_Omits_Validator_And_NextValidators()
        {
            XdcSubnetHeaderDecoder decoder = new();
            (XdcSubnetBlockHeader? original, byte[]? encodedBytes) = BuildHeaderAndDefaultEncode(decoder, true);

            // ForSealing encoding
            Rlp.ValueDecoderContext context = encodedBytes.AsRlpValueContext();
            XdcSubnetBlockHeader unencoded = (XdcSubnetBlockHeader)decoder.Decode(ref context, RlpBehaviors.ForSealing)!;

            Assert.That(unencoded.Validator, Is.Null, "ForSealing encoding should not contain Validator field.");
            Assert.That(unencoded.NextValidators, Is.Null, "ForSealing encoding should not contain NextValidators field.");
        }

    }
}
