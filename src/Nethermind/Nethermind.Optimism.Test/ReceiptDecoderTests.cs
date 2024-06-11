// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class ReceiptDecoderTests
{
    [TestCaseSource(nameof(DepositTxReceiptsSerializationTestCases))]
    public void Test_tx_network_form_receipts_properly_encoded_for_trie(byte[] rlp, bool includesNonce, bool includesVersion, bool shouldIncludeNonceAndVersionForTxTrie)
    {
        static OptimismTxReceipt TestNetworkEncodingRoundTrip(byte[] rlp, bool includesNonce, bool includesVersion)
        {
            OptimismReceiptMessageDecoder decoder = new();
            OptimismTxReceipt decodedReceipt = decoder.Decode(new RlpStream(rlp), RlpBehaviors.SkipTypedWrapping);

            RlpStream encodedRlp = new(decoder.GetLength(decodedReceipt, RlpBehaviors.SkipTypedWrapping));
            decoder.Encode(encodedRlp, decodedReceipt, RlpBehaviors.SkipTypedWrapping);

            Assert.Multiple(() =>
            {
                Assert.That(decodedReceipt.DepositNonce, includesNonce ? Is.Not.Null : Is.Null);
                Assert.That(decodedReceipt.DepositReceiptVersion, includesVersion ? Is.Not.Null : Is.Null);
                Assert.That(rlp, Is.EqualTo(encodedRlp.Data.ToArray()));
            });

            return decodedReceipt;
        }

        static OptimismTxReceipt TestStorageEncodingRoundTrip(OptimismTxReceipt decodedReceipt, bool includesNonce, bool includesVersion)
        {
            OptimismCompactReceiptStorageDecoder decoder = new();

            RlpStream encodedRlp = new(decoder.GetLength(decodedReceipt, RlpBehaviors.SkipTypedWrapping));
            decoder.Encode(encodedRlp, decodedReceipt, RlpBehaviors.SkipTypedWrapping);
            encodedRlp.Position = 0;

            OptimismTxReceipt decodedStorageReceipt = decoder.Decode(encodedRlp, RlpBehaviors.SkipTypedWrapping);

            Assert.Multiple(() =>
            {
                Assert.That(decodedStorageReceipt.DepositNonce, includesNonce ? Is.Not.Null : Is.Null);
                Assert.That(decodedStorageReceipt.DepositReceiptVersion, includesVersion ? Is.Not.Null : Is.Null);
            });

            Rlp.ValueDecoderContext valueDecoderCtx = new(encodedRlp.Data);
            decodedStorageReceipt = decoder.Decode(ref valueDecoderCtx, RlpBehaviors.SkipTypedWrapping);

            Assert.Multiple(() =>
            {
                Assert.That(decodedStorageReceipt.DepositNonce, includesNonce ? Is.Not.Null : Is.Null);
                Assert.That(decodedStorageReceipt.DepositReceiptVersion, includesVersion ? Is.Not.Null : Is.Null);
            });

            return decodedReceipt;
        }

        static void TestTrieEncoding(OptimismTxReceipt decodedReceipt, bool shouldIncludeNonceAndVersionForTxTrie)
        {
            OptimismReceiptTrieDecoder trieDecoder = new();
            RlpStream encodedTrieRlp = new(trieDecoder.GetLength(decodedReceipt, RlpBehaviors.SkipTypedWrapping));

            trieDecoder.Encode(encodedTrieRlp, decodedReceipt, RlpBehaviors.SkipTypedWrapping);
            encodedTrieRlp.Position = 0;

            OptimismTxReceipt decodedTrieReceipt = trieDecoder.Decode(encodedTrieRlp, RlpBehaviors.SkipTypedWrapping);

            Assert.Multiple(() =>
            {
                Assert.That(decodedTrieReceipt.DepositNonce, shouldIncludeNonceAndVersionForTxTrie ? Is.Not.Null : Is.Null);
                Assert.That(decodedTrieReceipt.DepositReceiptVersion, shouldIncludeNonceAndVersionForTxTrie ? Is.Not.Null : Is.Null);
            });
        }

        OptimismTxReceipt decodedReceipt = TestNetworkEncodingRoundTrip(rlp, includesNonce, includesVersion);
        TestStorageEncodingRoundTrip(decodedReceipt, includesNonce, includesVersion);
        TestTrieEncoding(decodedReceipt, shouldIncludeNonceAndVersionForTxTrie);
    }


    public static IEnumerable DepositTxReceiptsSerializationTestCases
    {
        get
        {
            yield return new TestCaseData(
                Bytes.FromHexString("7ef901090182f9f5b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c080"),
                true,
                false,
                false
                )
            {
                TestName = "1st OP Sepolia block receipt"
            };

            yield return new TestCaseData(
                Bytes.FromHexString("0x7ef9010c0182b729b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0830154f4"),
                true,
                false,
                false
                )
            {
                TestName = "Regolith receipt"
            };

            yield return new TestCaseData(
               Bytes.FromHexString("0x7ef903660183023676b9010000000000000000000000000000000000000000000000000000000000001000000000000000000080000000000000000001000000000000000000000000000204000200000004000000000000000000000000000000000000000000000000000100000000020000400000000000020800000000000000000000000008000000000000000000004000400000020000000001800000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000100000000000002100000000000000000000020001000100000040000000000000000000000000000000000000000000008000000f9025af9011d944200000000000000000000000000000000000010f884a0b0444523268717a02698be47d0803aa7468c00acbed2f8bd93a0459cde61dd89a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000deaddeaddeaddeaddeaddeaddeaddeaddead0000a000000000000000000000000072fb15f502af58765015972a85f2c58551ef3fa1b88000000000000000000000000072fb15f502af58765015972a85f2c58551ef3fa1000000000000000000000000000000000000000000000000016345785d8a000000000000000000000000000000000000000000000000000000000000000000600000000000000000000000000000000000000000000000000000000000000000f8dc944200000000000000000000000000000000000010f863a031b2166ff604fc5672ea5df08a78081d2bc6d746cadce880747f3643d819e83da000000000000000000000000072fb15f502af58765015972a85f2c58551ef3fa1a000000000000000000000000072fb15f502af58765015972a85f2c58551ef3fa1b860000000000000000000000000000000000000000000000000016345785d8a000000000000000000000000000000000000000000000000000000000000000000400000000000000000000000000000000000000000000000000000000000000000f85a944200000000000000000000000000000000000007f842a04641df4a962071e12719d8c8c8e5ac7fc4d97b927346a3d7a335b1f7517e133ca0c056e47e441542720e5a953ab9fcf1cc3de86fb1d3078293fb9708e6e77816938080"),
               true,
               false,
               false
               )
            {
                TestName = "Regolith receipt 2"
            };

            yield return new TestCaseData(
                Bytes.FromHexString("0xf901090183011711b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0"),
                false,
                false,
                false
                )
            {
                TestName = "Regolith receipt of a regular tx"
            };

            yield return new TestCaseData(
                Bytes.FromHexString("7ef9010d0182ab7bb9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c083b2557501"),
                true,
                true,
                true
                )
            {
                TestName = "Canyon receipt"
            };
        }
    }
}
