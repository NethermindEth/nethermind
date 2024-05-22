// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public partial class ReceiptDecoderTests
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
