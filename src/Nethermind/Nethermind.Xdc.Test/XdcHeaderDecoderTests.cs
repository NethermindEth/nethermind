using FluentAssertions;
using NUnit.Framework;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

using Nethermind.Core.Test.Builders;

namespace Nethermind.Xdc.Test
{
    [TestFixture]
    public class XdcHeaderRlpCodecTests
    {
        private static (XdcBlockHeader Header, byte[] Bytes) BuildHeaderAndDefaultEncode(XdcHeaderDecoder codec, bool includeBaseFee = true)
        {
            XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader;
            XdcBlockHeader header = (includeBaseFee ? builder.WithBaseFee((UInt256)1_000_000_000) : builder).TestObject;

            Rlp encoded = codec.Encode(header);
            return (header, encoded.Bytes);
        }

        [Test]
        public void EncodeDecode_RoundTrip_Matches_AllFields()
        {
            var codec = new XdcHeaderDecoder();
            var (original, encodedBytes) = BuildHeaderAndDefaultEncode(codec);

            // Decode
            var stream = new RlpStream(encodedBytes);
            BlockHeader? decodedBase = codec.Decode(stream);
            Assert.That(decodedBase, Is.Not.Null, "The decoded header should not be null.");
            Assert.That(decodedBase, Is.InstanceOf<XdcBlockHeader>(), "The decoded header should be an instance of XdcBlockHeader.");

            var decoded = (XdcBlockHeader)decodedBase!;

            // Hash is excluded since decoder sets it from RLP, but original is often not set
            decoded.Should().BeEquivalentTo(original, options => options.Excluding(h => h.Hash));
        }

        [Test]
        public void No_BaseFee()
        {
            var codec = new XdcHeaderDecoder();
            var (original, encodedBytes) = BuildHeaderAndDefaultEncode(codec, false);

            // Decode back
            var stream = new RlpStream(encodedBytes);
            var decoded = (XdcBlockHeader)codec.Decode(stream)!;

            Assert.That(decoded.BaseFeePerGas.IsZero, "BaseFeePerGas should be zero when omitted.");
        }

        [Test]
        public void TotalLength_Equals_GetLength()
        {
            var codec = new XdcHeaderDecoder();
            var (header, encodedBytes) = BuildHeaderAndDefaultEncode(codec);

            // compare to GetLength
            int expectedTotal = codec.GetLength(header, RlpBehaviors.None);
            Assert.That(encodedBytes.Length, Is.EqualTo(expectedTotal), "Encoded total length should match GetLength().");
        }

        [Test]
        public void Encode_ForSealing_Omits_MixHash_And_Nonce()
        {
            var codec = new XdcHeaderDecoder();
            var (header, encodedBytes) = BuildHeaderAndDefaultEncode(codec);
            int fullLen = encodedBytes.Length;

            // ForSealing encoding
            Rlp sealing = codec.Encode(header, RlpBehaviors.ForSealing);
            int sealingLen = sealing.Bytes.Length;

            int mixPart = Rlp.LengthOf(header.MixHash) + Rlp.LengthOfNonce(header.Nonce);
            Assert.That(fullLen - mixPart, Is.EqualTo(sealingLen),
                "ForSealing encoding should be shorter by MixHash+Nonce RLP sizes.");
        }
    }
}
