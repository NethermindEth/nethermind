// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
#pragma warning disable 618

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class ReceiptArrayDecoderTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void Legacy_missing_receipt_is_preserved_for_migration(bool compactEncoding)
        {
            byte[] encoded = compactEncoding
                ? [ReceiptArrayStorageDecoder.CompactEncoding, Rlp.EmptyListByte, Rlp.EmptyListByte]
                : [0xc1, Rlp.EmptyListByte];
            ReceiptArrayStorageDecoder decoder = new(compactEncoding);
            Span<byte> encodedSpan = encoded;
            TxReceipt?[] receipts = decoder.DecodeAllowingMissing(in encodedSpan);

            Assert.That(receipts, Is.EqualTo(new TxReceipt?[] { null }));
        }

        [Test]
        public void Legacy_missing_receipt_is_rejected_by_normal_decoder()
        {
            byte[] encoded = [0xc1, Rlp.EmptyListByte];
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: false);

            Assert.That(() => Decode(decoder, encoded), Throws.TypeOf<RlpException>());
        }

        [Test]
        public void Legacy_trailing_missing_receipt_is_omitted_by_normal_compact_decoder()
        {
            byte[] encoded = [ReceiptArrayStorageDecoder.CompactEncoding, Rlp.EmptyListByte, Rlp.EmptyListByte];
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: true);
            Span<byte> encodedSpan = encoded;

            Assert.That(decoder.Decode(in encodedSpan), Is.Empty);
        }

        private static void Decode(ReceiptArrayStorageDecoder decoder, byte[] encoded)
        {
            Span<byte> encodedSpan = encoded;
            _ = decoder.Decode(in encodedSpan);
        }

        [Test]
        public void Can_do_roundtrip_storage(
            [Values(RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts, RlpBehaviors.Storage)] RlpBehaviors encodeBehaviors,
            [Values(true, false)] bool compactEncoding,
            [Values(true, false)] bool withError
        )
        {
            TxReceipt GetExpected()
            {
                ReceiptBuilder receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;

                if ((encodeBehaviors & RlpBehaviors.Eip658Receipts) != 0)
                {
                    receiptBuilder.WithState(null!);
                }
                else
                {
                    receiptBuilder.WithStatusCode(0);
                }

                if (!withError)
                {
                    receiptBuilder.WithError(string.Empty);
                }

                if (compactEncoding)
                {
                    receiptBuilder.WithBlockHash(null);
                    receiptBuilder.WithBlockNumber(0);
                    receiptBuilder.WithTxType(0);
                    receiptBuilder.WithTransactionHash(null);
                    receiptBuilder.WithIndex(0);
                    receiptBuilder.WithGasUsed(0);
                    receiptBuilder.WithContractAddress(null);
                    receiptBuilder.WithRecipient(null);
                    receiptBuilder.WithError(null);
                }

                receiptBuilder.WithCalculatedBloom();
                return receiptBuilder.TestObject;
            }

            TxReceipt[] GetExpectedArray() => new[] { GetExpected(), GetExpected() };

            TxReceipt BuildReceipt()
            {
                ReceiptBuilder receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;
                if (!withError)
                {
                    receiptBuilder.WithError(string.Empty);
                }

                receiptBuilder.WithCalculatedBloom();
                return receiptBuilder.TestObject;
            }

            TxReceipt[] txReceipts = { BuildReceipt(), BuildReceipt() };

            ReceiptArrayStorageDecoder encoder = new(compactEncoding);
            using ArrayPoolSpan<byte> rlp = encoder.EncodeToArrayPoolSpan(txReceipts, encodeBehaviors);

            ReceiptArrayStorageDecoder decoder = new();
            RlpReader ctx = new((ReadOnlySpan<byte>)rlp);
            TxReceipt[] deserialized = decoder.DecodeGuardNotNull(ref ctx, RlpBehaviors.Storage);

            deserialized.AssertEquivalentTo(GetExpectedArray());
        }
    }
}
