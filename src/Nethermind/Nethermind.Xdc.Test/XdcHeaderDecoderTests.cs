// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class XdcHeaderDecoderTests
    {
        private static (XdcBlockHeader Header, byte[] Bytes) BuildHeaderAndDefaultEncode(XdcHeaderDecoder codec,
            Action<XdcBlockHeaderBuilder>? buildCallback = null, bool includeBaseFee = true)
        {
            XdcBlockHeaderBuilder builder = Build.A.XdcBlockHeader();

            if (includeBaseFee)
                builder.WithBaseFee((UInt256)1_000_000_000);
            buildCallback?.Invoke(builder);

            XdcBlockHeader header = builder.TestObject;

            Rlp encoded = codec.Encode(header);
            return (header, encoded.Bytes);
        }

        [Test]
        public void EncodeDecode_RoundTrip_Matches_AllFields()
        {
            XdcHeaderDecoder codec = new();
            (XdcBlockHeader? original, byte[]? encodedBytes) = BuildHeaderAndDefaultEncode(codec, b => b
                .WithValidators([TestItem.AddressA, TestItem.AddressB, TestItem.AddressC])
                .WithPenalties([TestItem.AddressD, TestItem.AddressE, TestItem.AddressF])
                .WithExtraConsensusData(new ExtraFieldsV2(1, Build.A.QuorumCertificate()
                    .WithBlockInfo(new BlockRoundInfo(Hash256.Zero, 1, 0))
                    .WithSignatures(TestItem.RandomSignatureA, TestItem.RandomSignatureB)
                    .TestObject)
                )
            );

            // Decode
            RlpReader context = new(encodedBytes);
            BlockHeader? decodedBase = codec.Decode(ref context);
            Assert.That(decodedBase, Is.Not.Null, "The decoded header should not be null.");
            Assert.That(decodedBase, Is.InstanceOf<XdcBlockHeader>(), "The decoded header should be an instance of XdcBlockHeader.");

            XdcBlockHeader decoded = (XdcBlockHeader)decodedBase!;

            Assert.That(decoded, Is.EqualTo(original).UsingXdcComparer(compareHash: false));
        }

        [Test]
        public void No_BaseFee()
        {
            XdcHeaderDecoder codec = new();
            (XdcBlockHeader? original, byte[]? encodedBytes) = BuildHeaderAndDefaultEncode(codec, includeBaseFee: false);

            // Decode back
            RlpReader context = new(encodedBytes);
            XdcBlockHeader decoded = (XdcBlockHeader)codec.Decode(ref context)!;

            Assert.That(decoded.BaseFeePerGas.IsZero, "BaseFeePerGas should be zero when omitted.");
        }

        [Test]
        public void TotalLength_Equals_GetLength()
        {
            XdcHeaderDecoder codec = new();
            (XdcBlockHeader? header, byte[]? encodedBytes) = BuildHeaderAndDefaultEncode(codec);

            // compare to GetLength
            int expectedTotal = codec.GetLength(header, RlpBehaviors.None);
            Assert.That(encodedBytes.Length, Is.EqualTo(expectedTotal), "Encoded total length should match GetLength().");
        }

        [Test]
        public void Encode_ForSealing_Omits_Validator()
        {
            XdcHeaderDecoder decoder = new();
            (XdcBlockHeader? header, byte[]? encodedBytes) = BuildHeaderAndDefaultEncode(decoder);
            int fullLen = encodedBytes.Length;

            // ForSealing encoding
            Rlp encoded = decoder.Encode(header, RlpBehaviors.ForSealing);
            RlpReader context = new(encoded.Bytes);
            XdcBlockHeader unencoded = (XdcBlockHeader)decoder.Decode(ref context, RlpBehaviors.ForSealing)!;

            Assert.That(unencoded.Validator, Is.Null,
                "ForSealing encoding should not contain Validator field.");
        }

        [TestCase("0xf90258a00000000000000000000000000000000000000000000000000000000000000000a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0efb190856ff185dded722e2dca183304c92fd7ac25f2ef5ea8ff9d518ba85693a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008302000080839896808080b86100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000000000000000880000000000000000808080")]
        [TestCase("0xf901f3a0683da113eb01cc0265a2c3399b49a80671b850c8b12739150fc6a1d2ca16b7d3a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0efb190856ff185dded722e2dca183304c92fd7ac25f2ef5ea8ff9d518ba85693a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001018398bca4800a80a00000000000000000000000000000000000000000000000000000000000000000880000000000000000808080")]
        public void Encode_Xdc_Rlp_Decodes_Correctly(string hexRlp)
        {
            XdcHeaderDecoder decoder = new();

            RlpReader context = new(Bytes.FromHexString(hexRlp));
            XdcBlockHeader? unencoded = (XdcBlockHeader?)decoder.Decode(ref context);

            string encoded = decoder.Encode(unencoded).ToString();

            Assert.That(encoded, Is.EqualTo(hexRlp));
        }

        [TestCase(nameof(BlockHeader.ParentHash))]
        [TestCase(nameof(BlockHeader.UnclesHash))]
        [TestCase(nameof(BlockHeader.Beneficiary))]
        [TestCase(nameof(BlockHeader.StateRoot))]
        [TestCase(nameof(BlockHeader.TxRoot))]
        [TestCase(nameof(BlockHeader.ReceiptsRoot))]
        [TestCase(nameof(BlockHeader.Bloom))]
        [TestCase(nameof(BlockHeader.MixHash))]
        public void Decode_throws_on_null_required_common_field(string fieldName)
        {
            XdcHeaderDecoder decoder = new();
            (XdcBlockHeader header, _) = BuildHeaderAndDefaultEncode(decoder);
            switch (fieldName)
            {
                case nameof(BlockHeader.ParentHash):
                    header.ParentHash = null;
                    break;
                case nameof(BlockHeader.UnclesHash):
                    header.UnclesHash = null;
                    break;
                case nameof(BlockHeader.Beneficiary):
                    header.Beneficiary = null;
                    break;
                case nameof(BlockHeader.StateRoot):
                    header.StateRoot = null;
                    break;
                case nameof(BlockHeader.TxRoot):
                    header.TxRoot = null;
                    break;
                case nameof(BlockHeader.ReceiptsRoot):
                    header.ReceiptsRoot = null;
                    break;
                case nameof(BlockHeader.Bloom):
                    header.Bloom = null;
                    break;
                case nameof(BlockHeader.MixHash):
                    header.MixHash = null;
                    break;
            }

            Rlp rlp = decoder.Encode(header);

            Assert.That(Decode, Throws.TypeOf<RlpException>());

            void Decode()
            {
                RlpReader reader = new(rlp.Bytes);
                decoder.Decode(ref reader);
            }
        }
    }
}
