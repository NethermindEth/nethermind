// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using NUnit.Framework;

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
            BlockHeader? decodedBase = codec.Decode((ReadOnlySpan<byte>)encodedBytes);
            Assert.That(decodedBase, Is.Not.Null, "The decoded header should not be null.");
            Assert.That(decodedBase, Is.InstanceOf<XdcBlockHeader>(), "The decoded header should be an instance of XdcBlockHeader.");

            XdcBlockHeader decoded = (XdcBlockHeader)decodedBase!;

            // Hash is excluded since decoder sets it from RLP, but original is often not set
            decoded.Should().BeEquivalentTo(original, options => options.Excluding(h => h.Hash));
        }

        [Test]
        public void No_BaseFee()
        {
            XdcHeaderDecoder codec = new();
            (XdcBlockHeader? original, byte[]? encodedBytes) = BuildHeaderAndDefaultEncode(codec, includeBaseFee: false);

            // Decode back
            XdcBlockHeader decoded = (XdcBlockHeader)codec.Decode((ReadOnlySpan<byte>)encodedBytes)!;

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
            XdcBlockHeader unencoded = (XdcBlockHeader)decoder.Decode((ReadOnlySpan<byte>)encoded.Bytes, RlpBehaviors.ForSealing)!;

            Assert.That(unencoded.Validator, Is.Null,
                "ForSealing encoding should not contain Validator field.");
        }

        [TestCase("0xf90258a00000000000000000000000000000000000000000000000000000000000000000a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0efb190856ff185dded722e2dca183304c92fd7ac25f2ef5ea8ff9d518ba85693a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008302000080839896808080b86100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000000000000000000000000000000880000000000000000808080")]
        [TestCase("0xf901f3a0683da113eb01cc0265a2c3399b49a80671b850c8b12739150fc6a1d2ca16b7d3a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0efb190856ff185dded722e2dca183304c92fd7ac25f2ef5ea8ff9d518ba85693a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001018398bca4800a80a00000000000000000000000000000000000000000000000000000000000000000880000000000000000808080")]
        public void Encode_Xdc_Rlp_Decodes_Correctly(string hexRlp)
        {
            XdcHeaderDecoder decoder = new();

            XdcBlockHeader? unencoded = (XdcBlockHeader?)decoder.Decode((ReadOnlySpan<byte>)Bytes.FromHexString(hexRlp));

            string encoded = decoder.Encode(unencoded).ToString();

            Assert.That(encoded, Is.EqualTo(hexRlp));
        }
    }
}
