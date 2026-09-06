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
        public void Legacy_missing_receipt_is_omitted_by_normal_decoder()
        {
            byte[] encoded = [0xc1, Rlp.EmptyListByte];
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: false);
            Span<byte> encodedSpan = encoded;

            Assert.That(decoder.Decode(in encodedSpan), Is.Empty);
        }

        [Test]
        public void Legacy_trailing_missing_receipt_is_omitted_by_normal_compact_decoder()
        {
            byte[] encoded = [ReceiptArrayStorageDecoder.CompactEncoding, Rlp.EmptyListByte, Rlp.EmptyListByte];
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: true);
            Span<byte> encodedSpan = encoded;

            Assert.That(decoder.Decode(in encodedSpan), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Normal_decoder_stops_at_first_legacy_missing_receipt(bool compactEncoding)
        {
            TxReceipt first = Build.A.Receipt.WithLogs().TestObject;
            TxReceipt afterGap = Build.A.Receipt.WithLogs().TestObject;
            ReceiptArrayStorageDecoder decoder = new(compactEncoding);
            byte[] encoded = decoder.EncodeAsBytes([first, null!, afterGap], RlpBehaviors.Storage);
            Span<byte> encodedSpan = encoded;

            TxReceipt[] decoded = decoder.Decode(in encodedSpan);

            Assert.That(decoded, Has.Length.EqualTo(1));
        }

        [Test]
        public void Migration_decoder_ignores_bytes_after_compact_trailing_missing_receipt()
        {
            byte[] encoded =
            [
                ReceiptArrayStorageDecoder.CompactEncoding,
                Rlp.EmptyListByte,
                Rlp.EmptyListByte,
                0x01
            ];
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: true);
            Span<byte> encodedSpan = encoded;

            Assert.That(decoder.DecodeAllowingMissing(in encodedSpan), Is.EqualTo(new TxReceipt?[] { null }));
        }

        [Test]
        public void Migration_decoder_does_not_treat_compact_buffer_residue_as_an_extra_receipt()
        {
            const int boundaryContentLength = 56;
            TxReceipt finalReceipt = Build.A.Receipt.WithLogs().TestObject;
            int finalReceiptLength = CompactReceiptStorageDecoder.Instance.GetLength(
                finalReceipt,
                RlpBehaviors.Storage);
            TxReceipt[] receipts = new TxReceipt[boundaryContentLength - finalReceiptLength + 1];
            receipts[^1] = finalReceipt;
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: true);
            byte[] encoded = decoder.EncodeAsBytes(receipts, RlpBehaviors.Storage);
            Array.Resize(ref encoded, encoded.Length + 1);
            encoded[^1] = Rlp.EmptyListByte;
            Span<byte> encodedSpan = encoded;

            TxReceipt?[] decoded = decoder.DecodeAllowingMissing(in encodedSpan);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded, Has.Length.EqualTo(receipts.Length));
                Assert.That(decoded[^1], Is.Not.Null);
            }
        }

        [TestCase(56)]
        [TestCase(256)]
        [TestCase(65536)]
        public void Compact_storage_length_matches_bytes_written_at_sequence_boundaries(int contentLength)
        {
            TxReceipt[] receipts = new TxReceipt[contentLength];
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: true);
            int encodedLength = decoder.GetLength(receipts, RlpBehaviors.Storage);
            byte[] encoded = new byte[encodedLength];
            RlpWriter writer = new(encoded);

            decoder.Encode(ref writer, receipts, RlpBehaviors.Storage);

            Assert.That(writer.Position, Is.EqualTo(encodedLength));
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
