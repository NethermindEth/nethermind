// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
#pragma warning disable 618

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class ReceiptArrayDecoderTests
    {
        [Test]
        public void Legacy_missing_receipt_is_preserved_for_migration([Values] bool compactEncoding)
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

        [Test]
        public void Normal_decoder_stops_at_first_legacy_missing_receipt([Values] bool compactEncoding)
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
            int finalReceiptLength = CompactReceiptStorageDecoder.Instance.GetEncodedLength(
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

        [Test]
        public void Compact_storage_length_matches_bytes_written_at_sequence_boundaries([Values(56, 256, 65536)] int contentLength)
        {
            TxReceipt[] receipts = new TxReceipt[contentLength];
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: true);
            int encodedLength = decoder.GetEncodedLength(receipts, RlpBehaviors.Storage);
            byte[] encoded = new byte[encodedLength];
            scoped RlpWriter writer = new(encoded);
            LengthStack lengths = new(stackalloc int[LengthStack.DefaultStackallocLength]);

            decoder.PopulateAndEncode(ref writer, receipts, ref lengths, RlpBehaviors.Storage);
            lengths.Dispose();

            Assert.That(writer.Position, Is.EqualTo(encodedLength));
        }

        [Test]
        public void Compact_storage_writes_the_transaction_hashes_when_every_receipt_has_one([Values(1, 2, 3)] int count)
        {
            TxReceipt[] receipts = BuildReceipts(count);
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: true);

            byte[] encoded = decoder.EncodeAsBytes(receipts, RlpBehaviors.Storage);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(encoded[0], Is.EqualTo(ReceiptArrayStorageDecoder.CompactEncodingWithTxHashes));
                Assert.That(encoded, Has.Length.EqualTo(decoder.GetEncodedLength(receipts, RlpBehaviors.Storage)));
            }

            RlpReader reader = new(encoded);
            Assert.That(ReceiptArrayStorageDecoder.TrySkipCompactPrefix(ref reader, out ReadOnlySpan<byte> txHashes), Is.True);
            using (Assert.EnterMultipleScope())
            {
                for (int i = 0; i < count; i++)
                {
                    Assert.That(txHashes.Slice(i * Hash256.Size, Hash256.Size).ToArray(), Is.EqualTo(receipts[i].TxHash!.BytesToArray()));
                }
            }
        }

        [Test]
        public void Compact_storage_keeps_the_plain_marker_when_a_receipt_has_no_transaction_hash()
        {
            TxReceipt[] receipts = BuildReceipts(2);
            receipts[1].TxHash = null;

            byte[] encoded = new ReceiptArrayStorageDecoder(compactEncoding: true).EncodeAsBytes(receipts, RlpBehaviors.Storage);

            Assert.That(encoded[0], Is.EqualTo(ReceiptArrayStorageDecoder.CompactEncoding));
        }

        [Test]
        public void Compact_storage_without_transaction_hashes_keeps_the_plain_marker_when_every_receipt_has_one()
        {
            TxReceipt[] receipts = BuildReceipts(2);

            byte[] encoded = ReceiptArrayStorageDecoder.WithoutTxHashes.EncodeAsBytes(receipts, RlpBehaviors.Storage);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(encoded[0], Is.EqualTo(ReceiptArrayStorageDecoder.CompactEncoding));
                Assert.That(encoded, Has.Length.EqualTo(ReceiptArrayStorageDecoder.WithoutTxHashes.GetEncodedLength(receipts, RlpBehaviors.Storage)));
                Assert.That(encoded, Is.EqualTo(ReceiptArrayStorageDecoder.Instance.EncodeAsBytes(WithoutTxHashes(receipts), RlpBehaviors.Storage)));
            }
        }

        [Test]
        public void Compact_values_with_and_without_transaction_hashes_decode_to_the_same_receipts()
        {
            TxReceipt[] receipts = BuildReceipts(3);
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: true);
            byte[] withHashes = decoder.EncodeAsBytes(receipts, RlpBehaviors.Storage);
            byte[] withoutHashes = decoder.EncodeAsBytes(WithoutTxHashes(receipts), RlpBehaviors.Storage);

            decoder.Decode(withHashes).AssertEquivalentTo(decoder.Decode(withoutHashes));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoder.DecodeAllowingMissing(withHashes), Has.Length.EqualTo(receipts.Length));
                Assert.That(ReceiptArrayStorageDecoder.IsCompactEncoding(withHashes), Is.True);
                Assert.That(ReceiptArrayStorageDecoder.IsCompactEncodingWithoutTxHashes(withHashes), Is.False);
                Assert.That(ReceiptArrayStorageDecoder.IsCompactEncodingWithoutTxHashes(withoutHashes), Is.True);
            }
        }

        [Test]
        public void Adding_transaction_hashes_to_a_compact_value_gives_the_bytes_of_a_direct_write([Values(1, 2, 3)] int count)
        {
            TxReceipt[] receipts = BuildReceipts(count);
            ReceiptArrayStorageDecoder decoder = new(compactEncoding: true);
            byte[] withoutHashes = decoder.EncodeAsBytes(WithoutTxHashes(receipts), RlpBehaviors.Storage);
            byte[] txHashes = receipts.SelectMany(static r => r.TxHash!.BytesToArray()).ToArray();

            byte[] migrated = ReceiptArrayStorageDecoder.AddTxHashes(withoutHashes, txHashes);

            Assert.That(migrated, Is.EqualTo(decoder.EncodeAsBytes(receipts, RlpBehaviors.Storage)));
        }

        private static TxReceipt[] BuildReceipts(int count) =>
            Enumerable.Range(0, count)
                .Select(static i => Build.A.Receipt.WithAllFieldsFilled.WithTransactionHash(Keccak.Compute([(byte)i])).WithCalculatedBloom().TestObject)
                .ToArray();

        private static TxReceipt[] WithoutTxHashes(TxReceipt[] receipts) =>
            receipts.Select(static r => new TxReceipt(r) { TxHash = null }).ToArray();

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
