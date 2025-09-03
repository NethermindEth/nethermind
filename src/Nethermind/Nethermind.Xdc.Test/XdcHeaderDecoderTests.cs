using FluentAssertions;
using NUnit.Framework;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

using Nethermind.Core.Test.Builders;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Xdc;
using Nethermind.Core.Crypto;

namespace Nethermind.Xdc.Test
{
    [TestFixture]
    public class XdcHeaderDecoderTests
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
        public void Encode_ForSealing_Omits_Validator()
        {
            var decoder = new XdcHeaderDecoder();
            var (header, encodedBytes) = BuildHeaderAndDefaultEncode(decoder);
            int fullLen = encodedBytes.Length;

            // ForSealing encoding
            Rlp encoded = decoder.Encode(header, RlpBehaviors.ForSealing);
            XdcBlockHeader unencoded = (XdcBlockHeader)decoder.Decode(new RlpStream(encoded.Bytes), RlpBehaviors.ForSealing)!;

            Assert.That(unencoded.Validator, Is.Null,
                "ForSealing encoding should not contain Validator field.");
        }

        [TestCase("0xf90258a00000000000000000000000000000000000000000000000000000000000000000a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0efb190856ff185dded722e2dca183304c92fd7ac25f2ef5ea8ff9d518ba85693a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008302000080839896808080b86100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000000000000000880000000000000000808080")]
        [TestCase("0xf901f3a0683da113eb01cc0265a2c3399b49a80671b850c8b12739150fc6a1d2ca16b7d3a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0efb190856ff185dded722e2dca183304c92fd7ac25f2ef5ea8ff9d518ba85693a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001018398bca4800a80a00000000000000000000000000000000000000000000000000000000000000000880000000000000000808080")]
        public void Encode_Xdc_Rlp_Decodes_Correctly(string hexRlp)
        {
            var decoder = new XdcHeaderDecoder();

            BlockHeader? unencoded = decoder.Decode(new RlpStream(Bytes.FromHexString(hexRlp)));

            string encoded = decoder.Encode(unencoded).ToString();

            Assert.That(encoded, Is.EqualTo(hexRlp));
        }        
    }
}
