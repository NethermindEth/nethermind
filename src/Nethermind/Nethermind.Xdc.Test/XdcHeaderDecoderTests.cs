using System;
using NUnit.Framework;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Test
{
    [TestFixture]
    public class XdcHeaderRlpCodecTests
    {
        private static XdcBlockHeader MakeHeader(bool includeBaseFee = true)
        {
            var parent   = new Hash256(new byte[32]);
            var uncles   = new Hash256(new byte[32]);
            var coinbase = new Address(new byte[20]);
            UInt256 diff = UInt256.One;
            long number  = 1;
            long gasLim  = 30_000_000;
            ulong ts     = 1_700_000_000;
            var extra= Array.Empty<byte>();

            var header = new XdcBlockHeader(parent, uncles, coinbase, in diff, number, gasLim, ts, extra)
            {
                StateRoot    = new Hash256(new byte[32]),
                TxRoot       = new Hash256(new byte[32]),
                ReceiptsRoot = new Hash256(new byte[32]),
                Bloom        = new Bloom(new byte[256]),
                GasUsed      = 21_000,
                MixHash      = new Hash256(new byte[32]),
                Nonce        = 0UL,
                Validators   = new byte[20 * 2],
                Validator    = new byte[20],
                Penalties    = Array.Empty<byte>(),
            };

            if (includeBaseFee)
            {
                header.BaseFeePerGas = (UInt256)1_000_000_000; // 1 gwei
            }

            return header;
        }

        [Test]
        public void EncodeDecode_RoundTrip_Matches_AllFields()
        {
            var codec = new XdcHeaderDecoder();
            XdcBlockHeader original = MakeHeader();

            // Encode
            Rlp r = codec.Encode(original);
            byte[] bytes = r.Bytes;

            // Decode
            var stream = new RlpStream(bytes);
            BlockHeader? decodedBase = codec.Decode(stream);
            Assert.That(decodedBase, Is.Not.Null, "The decoded header should not be null.");
            Assert.That(decodedBase,Is.InstanceOf<XdcBlockHeader>(), "The decoded header should be an instance of XdcBlockHeader.");

            var decoded = (XdcBlockHeader)decodedBase!;

            // Spot-check key fields
            Assert.That(original.ParentHash, Is.EqualTo(decoded.ParentHash), "The parent hash should be the same.");
            Assert.That(original.UnclesHash, Is.EqualTo(decoded.UnclesHash),  "The uncles hash should be the same.");
            Assert.That(original.Beneficiary, Is.EqualTo(decoded.Beneficiary),  "The beneficiary should be the same.");
            Assert.That(original.StateRoot, Is.EqualTo(decoded.StateRoot),  "The state root should be the same.");
            Assert.That(original.TxRoot, Is.EqualTo(decoded.TxRoot),   "The tx root should be the same.");
            Assert.That(original.ReceiptsRoot, Is.EqualTo(decoded.ReceiptsRoot),  "The receipts root should be the same.");
            Assert.That(original.Bloom, Is.EqualTo(decoded.Bloom),  "The bloom should be the same.");
            Assert.That(original.Difficulty, Is.EqualTo(decoded.Difficulty),  "The difficulty should be the same.");
            Assert.That(original.Number, Is.EqualTo(decoded.Number),  "The number should be the same.");
            Assert.That(original.GasLimit, Is.EqualTo(decoded.GasLimit),  "The gas limit should be the same.");
            Assert.That(original.GasUsed, Is.EqualTo(decoded.GasUsed),  "The gas used should be the same.");
            Assert.That(original.Timestamp, Is.EqualTo(decoded.Timestamp),  "The timestamp should be the same.");
            Assert.That(original.ExtraData, Is.EqualTo(decoded.ExtraData),  "The extra data should be the same.");
            Assert.That(original.MixHash, Is.EqualTo(decoded.MixHash),  "The mix hash should be the same.");
            Assert.That(original.Nonce, Is.EqualTo(decoded.Nonce),  "The nonce should be the same.");
            Assert.That(decoded.Validators, Is.EqualTo(original.Validators), "Validators should match.");
            Assert.That(original.Validator, Is.EqualTo(decoded.Validator), "Validator should match.");
            Assert.That(original.Penalties,Is.EqualTo(decoded.Penalties),"Penalties should match.");
            Assert.That(original.BaseFeePerGas, Is.EqualTo(decoded.BaseFeePerGas),"BaseFeePerGas should be the same.");
        }

        [Test]
        public void No_BaseFee()
        {
            var codec = new XdcHeaderDecoder();
            XdcBlockHeader header = MakeHeader(includeBaseFee: false);

            // Encode without base fee
            Rlp r = codec.Encode(header);
            byte[] bytes = r.Bytes;

            // Decode back
            var stream = new RlpStream(bytes);
            var decoded = (XdcBlockHeader)codec.Decode(stream)!;

            Assert.That(decoded.BaseFeePerGas.IsZero, "BaseFeePerGas should be zero when omitted.");
        }

        [Test]
        public void TotalLength_Equals_GetLength()
        {
            var codec = new XdcHeaderDecoder();
            XdcBlockHeader header = MakeHeader();

            // encode
            Rlp encoded = codec.Encode(header);
            byte[] bytes = encoded.Bytes;

            // compare to GetLength
            int expectedTotal = codec.GetLength(header, RlpBehaviors.None);
            Assert.That(bytes.Length, Is.EqualTo(expectedTotal), "Encoded total length should match GetLength().");
        }

        [Test]
        public void Encode_ForSealing_Omits_MixHash_And_Nonce()
        {
            var codec = new XdcHeaderDecoder();
            XdcBlockHeader header = MakeHeader(includeBaseFee: true);

            // Full encoding
            Rlp full = codec.Encode(header, RlpBehaviors.None);
            int fullLen = full.Bytes.Length;

            // ForSealing encoding
            Rlp sealing = codec.Encode(header, RlpBehaviors.ForSealing);
            int sealingLen = sealing.Bytes.Length;

            int mixPart = Rlp.LengthOf(header.MixHash) + Rlp.LengthOfNonce(header.Nonce);
            Assert.That(fullLen - mixPart, Is.EqualTo(sealingLen),
                "ForSealing encoding should be shorter by MixHash+Nonce RLP sizes.");
        }
    }
}
