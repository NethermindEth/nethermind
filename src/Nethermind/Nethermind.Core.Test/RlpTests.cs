// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Encoding;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class RlpTests
    {
        private static readonly TxDecoder TransactionDecoder = TxDecoder.Instance;

        public record DecoderCase(string Name, Func<RlpReader, dynamic> Invoke, int? Size)
        {
            public override string ToString() => Name;
        }

        [Test]
        public void Decodes_uint256_of_every_payload_length([Range(0, 32)] int byteLength)
        {
            UInt256 value = UInt256OfByteLength(byteLength);

            UInt256 decoded = Rlp.Decode<UInt256>(Rlp.Encode(value).Bytes);

            Assert.That(decoded, Is.EqualTo(value));
        }


        [Test]
        public void Sequence_reads_chain_without_changing_the_input([Values(0, 3)] int start)
        {
            byte[] encoded = [0xc7, 0x01, 0x81, 0x80, 0x83, 0x61, 0x62, 0x63];
            byte[] buffer = [.. new byte[start], .. encoded];
            RlpReader outer = new(buffer.AsSpan(start));

            RlpReader reader = outer.EnterSequence(out int itemLength);
            RlpReader reread = reader;
            ulong first = reader.DecodeULong();
            UInt256 second = reader.DecodeUInt256();
            ReadOnlySpan<byte> third = reader.DecodeByteArraySpan();
            ulong rereadFirst = reread.DecodeULong();
            outer.ExitSequence(reader, itemLength);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(itemLength, Is.EqualTo(encoded.Length));
                Assert.That(first, Is.EqualTo(1));
                Assert.That(second, Is.EqualTo((UInt256)128));
                Assert.That(third.ToArray(), Is.EqualTo(new byte[] { 0x61, 0x62, 0x63 }));
                Assert.That(outer.BytesRead, Is.EqualTo(encoded.Length));
                Assert.That(reread.BytesRead, Is.EqualTo(1));
                Assert.That(rereadFirst, Is.EqualTo(first));
                Assert.That(buffer.AsSpan(start).ToArray(), Is.EqualTo(encoded));
            }
        }

        [Test]
        public void Empty_sequence_marks_null_and_empty_string_does_not()
        {
            RlpReader nullReader = new([Rlp.EmptyListByte, Rlp.EmptyByteArrayByte]);
            bool consumedNull = nullReader.TryReadEmptySequence();

            RlpReader emptyReader = new([Rlp.EmptyByteArrayByte]);
            bool consumedEmptyString = emptyReader.TryReadEmptySequence();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(consumedNull, Is.True);
                Assert.That(nullReader.BytesRead, Is.EqualTo(1));
                Assert.That(consumedEmptyString, Is.False);
                Assert.That(emptyReader.BytesRead, Is.Zero);
            }
        }

        [Test]
        public void Reader_extensions_decode_each_value_type()
        {
            byte[] byteValueRlp = [1];
            RlpReader reader = new(byteValueRlp);
            Assert.That((reader.DecodeByte(), reader.BytesRead), Is.EqualTo(((byte)1, byteValueRlp.Length)));

            byte[] hashRlp = [160, .. Keccak.EmptyTreeHash.Bytes];
            reader = new(hashRlp);
            Assert.That((reader.DecodeValueKeccak(), reader.BytesRead), Is.EqualTo(((ValueHash256?)Keccak.EmptyTreeHash.ValueHash256, hashRlp.Length)));

            byte[] zeroPrefixHashRlp = [1];
            reader = new(zeroPrefixHashRlp);
            byte[] expectedHashBytes = new byte[Hash256.Size];
            expectedHashBytes[^1] = 1;
            Assert.That((reader.DecodeZeroPrefixKeccak(), reader.BytesRead), Is.EqualTo((new Hash256(expectedHashBytes), zeroPrefixHashRlp.Length)));

            byte[] bloomRlp = ExpectedBloom(Bloom.Empty);
            reader = new(bloomRlp);
            Assert.That((reader.DecodeBloom(), reader.BytesRead), Is.EqualTo((Bloom.Empty, bloomRlp.Length)));

            byte[] stringRlp = [0x83, 0x61, 0x62, 0x63];
            reader = new(stringRlp);
            Assert.That((reader.DecodeString(), reader.BytesRead), Is.EqualTo(("abc", stringRlp.Length)));
        }

        [Test]
        public void Reader_does_not_move_past_an_item_that_fails([Values] bool asULong)
        {
            byte[] buffer = [0x01, 0x81, 0x01];
            RlpReader reader = new(buffer);
            if (asULong) reader.DecodeULong();
            else reader.DecodeByteArraySpan();

            bool threw = false;
            try
            {
                if (asULong) reader.DecodeULong();
                else reader.DecodeByteArraySpan();
            }
            catch (RlpReaderException)
            {
                threw = true;
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(threw, Is.True);
                Assert.That(reader.BytesRead, Is.EqualTo(1));
            }
        }

        /// <summary>Builds a value whose canonical big-endian encoding is exactly that many bytes.</summary>
        private static UInt256 UInt256OfByteLength(int byteLength)
        {
            if (byteLength == 0)
            {
                return UInt256.Zero;
            }

            Span<byte> bytes = stackalloc byte[byteLength];
            for (int i = 0; i < byteLength; i++)
            {
                // Distinct per position so a word assembled from the wrong offset cannot still match.
                bytes[i] = (byte)(i + 1);
            }

            bytes[0] = 0xab; // non-zero, so the encoding is canonical at this length
            return new UInt256(bytes, isBigEndian: true);
        }

        [Test]
        public void InternKeccak_discriminators_match_the_interned_hashes()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    RlpReaderExtensions.FirstWord(Keccak.OfAnEmptyString.Bytes),
                    Is.EqualTo(RlpReaderExtensions.OfAnEmptyStringFirstWord));
                Assert.That(
                    RlpReaderExtensions.FirstWord(Keccak.EmptyTreeHash.Bytes),
                    Is.EqualTo(RlpReaderExtensions.EmptyTreeHashFirstWord));
            }
        }

        // The first row is the boundaries of the length prefix. The second is one value per byte width,
        // each distinct per position behind a non-zero leading byte, so the encoding stays canonical at
        // that width and a word assembled from the wrong offset cannot still match.
        [Test]
        public void Decodes_ulong_round_trip(
            [Values(
                0UL, 1UL, 127UL, 128UL, 255UL, 256UL, ulong.MaxValue,
                0xabUL, 0xab02UL, 0xab0203UL, 0xab020304UL,
                0xab02030405UL, 0xab0203040506UL, 0xab020304050607UL, 0xab02030405060708UL)]
            ulong value) =>
            Assert.That(Rlp.Decode<ulong>(Rlp.Encode(value).Bytes), Is.EqualTo(value));

        [Test]
        public void DecodeArray_rejects_more_items_than_the_limit()
        {
            RlpLimit limit = new(4);

            byte[] atLimit = Rlp.Encode(Enumerable.Range(0, limit.Limit).Select(i => Rlp.Encode(i)).ToArray()).Bytes;
            Assert.That(() =>
            {
                RlpReader reader = new(atLimit);
                reader.DecodeArray(static (ref RlpReader c) => c.DecodeInt(), limit: limit);
            }, Throws.Nothing);

            byte[] overLimit = Rlp.Encode(Enumerable.Range(0, limit.Limit + 1).Select(i => Rlp.Encode(i)).ToArray()).Bytes;
            Assert.That(() =>
            {
                RlpReader reader = new(overLimit);
                reader.DecodeArray(static (ref RlpReader c) => c.DecodeInt(), limit: limit);
            }, Throws.TypeOf<RlpLimitException>());
        }

        [Test]
        public void GuardLimit_without_a_limit_applies_the_default_limit()
        {
            int limit = RlpLimit.DefaultLimit.Limit;
            Assert.That(() => Rlp.GuardLimit(limit, int.MaxValue), Throws.Nothing);
            Assert.That(() => Rlp.GuardLimit(limit + 1, int.MaxValue), Throws.TypeOf<RlpLimitException>());
        }

        [Test]
        public void Serializing_sequences()
        {
            Rlp output = Rlp.Encode(
                Rlp.Encode(255L),
                Rlp.Encode(new byte[] { 255 }));
            Assert.That(output.Bytes, Is.EqualTo(new byte[] { 196, 129, 255, 129, 255 }));
        }

        [Test]
        public void Serializing_empty_sequence()
        {
            Rlp output = Rlp.Encode(Array.Empty<Rlp>());
            Assert.That(output.Bytes, Is.EqualTo(new byte[] { 192 }));
        }

        [Test]
        public void Serializing_sequence_with_one_int_regression()
        {
            Rlp output = Rlp.Encode(new[] { Rlp.Encode(1) });
            Assert.That(output.Bytes, Is.EqualTo(new byte[] { 193, 1 }));
        }

        [TestCase("")]
        [TestCase("00")]
        [TestCase("05")]
        [TestCase("7f")]
        [TestCase("80")]
        [TestCase("ff")]
        [TestCase("0102")]
        [TestCase("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
        public void Encode_byte_string_into_span_matches_allocating_overload(string hex)
        {
            byte[] input = Extensions.Bytes.FromHexString(hex);
            byte[] expected = Rlp.Encode((ReadOnlySpan<byte>)input).Bytes;

            Span<byte> output = stackalloc byte[Math.Max(1, expected.Length)];
            int written = Rlp.Encode(input, output);

            Assert.That(written, Is.EqualTo(expected.Length));
            Assert.That(output[..written], Is.SequenceEqualTo(expected));
        }

        [TestCaseSource(nameof(ValueWriterByteArrayCases))]
        public void RlpWriter_encodes_byte_spans_like_Rlp(byte[] value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode((ReadOnlySpan<byte>)value);

            AssertValueWriterMatchesExpected(writer, buffer, Rlp.Encode((ReadOnlySpan<byte>)value).Bytes);
        }

        [Test]
        public void RlpWriter_encodes_ulong_like_Rlp([Values(0UL, 1UL, 127UL, 128UL, 255UL, 256UL, ulong.MaxValue)] ulong value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode(value);

            Span<byte> expected = stackalloc byte[9];
            expected = Rlp.Encode(value, expected);
            AssertValueWriterMatchesExpected(writer, buffer, expected);
        }

        [Test]
        public void EncodeToCappedArray_encodes_int_like_Rlp([Values(0, 1, 127, 128, 1023, 1024, 65536, int.MaxValue)] int value) =>
            Assert.That(Rlp.EncodeToCappedArray(value), Is.SequenceEqualTo(Rlp.Encode(value).Bytes));

        [TestCaseSource(nameof(ValueWriterUInt256Cases))]
        public void RlpWriter_encodes_uint256_like_Rlp(UInt256 value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode(in value);

            AssertValueWriterMatchesExpected(writer, buffer, Rlp.Encode(in value).Bytes);
        }

        [TestCaseSource(nameof(ValueWriterHashCases))]
        public void RlpWriter_encodes_hash_like_Rlp(Hash256? value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode(value);

            AssertValueWriterMatchesExpected(writer, buffer, Rlp.Encode(value).Bytes);
        }

        [TestCaseSource(nameof(ValueWriterValueHashCases))]
        public void RlpWriter_encodes_value_hash_like_Rlp(ValueHash256? value)
        {
            int length = Rlp.LengthOf(in value);

            byte[] buffer = new byte[length];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode(in value);

            AssertValueWriterMatchesExpected(writer, buffer, ExpectedValueHash(value));
        }

        [TestCaseSource(nameof(ValueWriterValueHashCases))]
        public void RlpReader_roundtrips_value_hash(ValueHash256? value)
        {
            byte[] buffer = new byte[Rlp.LengthOf(in value)];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode(in value);

            RlpReader reader = new(buffer);

            Assert.That(reader.DecodeValueKeccak(), Is.EqualTo(value));

            if (value is null)
            {
                Assert.Throws<RlpReaderException>(() =>
                {
                    RlpReader nonNullReader = new(buffer);
                    nonNullReader.DecodeValueKeccakNonNull();
                });
            }
            else
            {
                RlpReader nonNullReader = new(buffer);
                Assert.That(nonNullReader.DecodeValueKeccakNonNull(), Is.EqualTo(value.Value));
            }
        }

        [TestCaseSource(nameof(ValueWriterHashCases))]
        public void RlpReader_roundtrips_hash(Hash256? value)
        {
            byte[] buffer = new byte[Rlp.LengthOf(value)];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode(value);

            RlpReader reader = new(buffer);
            Hash256? decoded = reader.DecodeKeccakOrNull();

            Assert.That(decoded, Is.EqualTo(value));
            if (value == Keccak.OfAnEmptyString || value == Keccak.EmptyTreeHash)
            {
                // The interned instances must come back by reference, which is the only observable
                // difference between comparing the payload as a span and as a ValueHash256.
                Assert.That(decoded, Is.SameAs(value));
            }
        }

        [TestCaseSource(nameof(ValueWriterAddressCases))]
        public void RlpWriter_encodes_address_like_Rlp(Address? value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode(value);

            AssertValueWriterMatchesExpected(writer, buffer, ExpectedAddress(value));
        }

        [TestCaseSource(nameof(ValueWriterBloomCases))]
        public void RlpWriter_encodes_bloom_like_Rlp(Bloom? value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode(value);

            AssertValueWriterMatchesExpected(writer, buffer, ExpectedBloom(value));
        }

        [TestCaseSource(nameof(ValueWriterByteArraySequenceCases))]
        public void RlpWriter_encodes_byte_array_sequences_like_Rlp(byte[][] value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            writer.Encode(value);

            AssertValueWriterMatchesExpected(writer, buffer, Rlp.Encode(value.Select(Rlp.Encode).ToArray()).Bytes);
        }

        [Test]
        [Explicit("That was a regression test but now it is failing again and cannot find the reason we needed this behaviour in the first place. Sync works all fine. Leaving it here as it may resurface - make sure to add more explanation to it in such case.")]
        public void Serializing_object_int_regression()
        {
            Rlp output = Rlp.Encode(new[] { Rlp.Encode(1) });
            Assert.That(output.Bytes, Is.EqualTo(new byte[] { 1 }));
        }

        [Test]
        public void Decode_integer(
            [ValueSource(nameof(IntegerDecoders))] DecoderCase decoder,
            [ValueSource(nameof(IntegerTestCases))] (byte[] rlp, string expectedHex) test,
            [Values(0, 1, 0xFF)] int position // test different offsets in Rlp
        )
        {
            int expectedSize = test.expectedHex.Length / 2 + test.expectedHex.Length % 2;
            if (decoder.Size is { } size && size < expectedSize)
                Assert.Ignore("Size over limit");

            byte[] rlp = position == 0 ? test.rlp : [.. Enumerable.Range(0, position).Select(static i => (byte)i), .. test.rlp];
            RlpReader context = new(rlp.AsSpan(position));

            Assert.That(
                decoder.Invoke(context).ToString("X").TrimStart('0'),
                Is.EqualTo(test.expectedHex.TrimStart('0'))
            );
        }

        [TestCaseSource(nameof(IntegerDecoders))]
        public void Decode_integer_oversize(DecoderCase decoder)
        {
            if (decoder.Size is not { } size)
            {
                Assert.Ignore("No size limit");
                return;
            }

            byte[] bytes = [.. Enumerable.Repeat<byte>(0x99, size + 2)];
            bytes[0] = (byte)(0x80 + size + 1);

            Assert.That(
                () => decoder.Invoke(new RlpReader(bytes)),
                Throws.InstanceOf<RlpReaderException>()
            );
        }

        [Test]
        public void Decode_integer_invalid(
            [ValueSource(nameof(IntegerDecoders))] DecoderCase decoder,
            [Values(
                new byte[] { 0x00 }, new byte[] { 0x81 }, new byte[] { 0xFF },
                new byte[] { 0x81, 0x00 }, new byte[] { 0x81, 0x01 }, new byte[] { 0x81, 0x0F },
                new byte[] { 0x81, 0x10 }, new byte[] { 0x81, 0x1F }, new byte[] { 0x81, 0x7F },
                new byte[] { 0x82, 0x00, 0x81 }, new byte[] { 0x82, 0x00, 0xFF },
                new byte[] { 0x83, 0x00, 0x01, 0x00 }, new byte[] { 0x84, 0x00, 0x00, 0x00, 0x01 },
                new byte[] { 0x82, 0x01 }
            )] byte[] bytes) =>
            Assert.That(
                () => decoder.Invoke(new RlpReader(bytes)),
                Throws.InstanceOf<RlpReaderException>().Or.InstanceOf<IndexOutOfRangeException>()
            );

        [Test]
        public void Length_of_uint()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Rlp.LengthOf(UInt256.Zero), Is.EqualTo(1));
                Assert.That(Rlp.LengthOf((UInt256)127), Is.EqualTo(1));
                Assert.That(Rlp.LengthOf((UInt256)128), Is.EqualTo(2));
            }

            UInt256 item = 255;
            for (int i = 0; i < 32; i++)
            {
                Assert.That(Rlp.LengthOf(item), Is.EqualTo(i + 2));
                item *= 256;
            }
        }

        [Test]
        public void Length_of_ulong_matches_spec([ValueSource(nameof(ULongValues))] ulong value)
        {
            int expected = SpecLengthOf(value);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Rlp.LengthOf(value), Is.EqualTo(expected), "ulong");
                Assert.That(Rlp.LengthOf((UInt256)value), Is.EqualTo(expected), "UInt256");
            }
        }

        /// <summary>Computes the RLP length of an unsigned integer.</summary>
        /// <remarks>
        /// Yellow Paper appendix B gives the length. A value below 0x80 uses one byte. Larger values use
        /// one prefix byte plus the minimal big-endian bytes. The loop shape is deliberately different from
        /// the production implementation. This prevents a defect shared with the production code.
        /// </remarks>
        private static int SpecLengthOf(ulong value)
        {
            if (value < 0x80)
            {
                return 1;
            }

            int byteCount = 0;
            for (ulong rest = value; rest != 0; rest >>= 8)
            {
                byteCount++;
            }

            return 1 + byteCount;
        }

        [Test]
        public void Single_byte_array_decoding_preserves_array_ownership([Range(0, 255)] int value)
        {
            byte[] encoded = value < 128 ? [(byte)value] : [0x81, (byte)value];
            RlpReader reader = new(encoded);
            byte[] decoded = reader.DecodeByteArray();
            RlpReader secondReader = new(encoded);
            byte[] expected = secondReader.DecodeByteArray();
            encoded[^1] ^= 0xff;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded, Is.EqualTo(new byte[] { (byte)value }));
                Assert.That(decoded, value < 128 ? Is.SameAs(expected) : Is.Not.SameAs(expected));
                Assert.That(reader.BytesRead, Is.EqualTo(encoded.Length));
            }
        }

        [Test]
        public void Single_byte_encoding_decoding()
        {
            byte item = 0;
            for (int i = 0; i < 128; i++)
            {
                Assert.That(Rlp.LengthOf(item), Is.EqualTo(1));
                Rlp data = Rlp.Encode(item);
                RlpReader rlp = new(data.Bytes);
                Assert.That(rlp.DecodeByte(), Is.EqualTo(item));

                item += 1;
            }

            for (int i = 128; i < 256; i++)
            {
                Assert.That(Rlp.LengthOf(item), Is.EqualTo(2));
                Rlp data = Rlp.Encode(item);
                RlpReader rlp = new(data.Bytes);
                Assert.That(rlp.DecodeByte(), Is.EqualTo(item));

                item += 1;
            }
        }

        [Test]
        public void Long_encode_decode([ValueSource(nameof(LongValues))] long value, [Values] bool useBuffer)
        {
            RlpReader context = useBuffer
                ? new(Rlp.Encode(value, stackalloc byte[9]))
                : new(Rlp.Encode(value).Bytes);

            long decoded = context.DecodeLong();

            Assert.That(decoded, Is.EqualTo(value));
        }

        [Test]
        public void ULong_encode_decode([ValueSource(nameof(ULongValues))] ulong value, [Values] bool useBuffer)
        {
            RlpReader context = useBuffer
                ? new(Rlp.Encode(value, stackalloc byte[9]))
                : new(Rlp.Encode(value).Bytes);

            ulong decoded = context.DecodeULong();

            Assert.That(decoded, Is.EqualTo(value));
        }

        [Test]
        public void Empty_byte_array()
        {
            byte[] bytes = [];
            Rlp rlp = Rlp.Encode(bytes);
            Rlp rlpSpan = Rlp.Encode(bytes.AsSpan());
            Rlp expectedResult = new(new byte[] { 128 });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rlp, Is.EqualTo(expectedResult), "byte array");
                Assert.That(rlpSpan, Is.EqualTo(expectedResult), "span");
            }
        }

        [Test]
        public void Byte_array_of_length_1_and_first_byte_value_less_than_128([Values(0, 1, 127)] byte value)
        {
            byte[] bytes = { value };
            Rlp rlp = Rlp.Encode(bytes);
            Rlp rlpSpan = Rlp.Encode(bytes.AsSpan());
            Rlp expectedResult = new(new[] { value });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rlp, Is.EqualTo(expectedResult), "byte array");
                Assert.That(rlpSpan, Is.EqualTo(expectedResult), "span");
            }
        }

        [Test]
        public void Byte_array_of_length_1_and_first_byte_value_equal_or_more_than_128([Values(128, 255)] byte value)
        {
            byte[] bytes = { value };
            Rlp rlp = Rlp.Encode(bytes);
            Rlp rlpSpan = Rlp.Encode(bytes.AsSpan());
            Rlp expectedResult = new(new[] { (byte)129, value });
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rlp, Is.EqualTo(expectedResult), "byte array");
                Assert.That(rlpSpan, Is.EqualTo(expectedResult), "span");
            }
        }

        [Test]
        public void Byte_array_of_length_55()
        {
            byte[] input = new byte[55];
            input[0] = 255;
            input[1] = 128;
            input[2] = 1;

            byte[] expectedResultBytes = new byte[1 + input.Length];
            expectedResultBytes[0] = (byte)(128 + input.Length);
            expectedResultBytes[1] = input[0];
            expectedResultBytes[2] = input[1];
            expectedResultBytes[3] = input[2];

            Rlp expectedResult = new(expectedResultBytes);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Rlp.Encode(input), Is.EqualTo(expectedResult), "byte array");
                Assert.That(Rlp.Encode(input.AsSpan()), Is.EqualTo(expectedResult), "span");
            }
        }

        [Test]
        public void Byte_array_of_length_56()
        {
            byte[] input = new byte[56];
            input[0] = 255;
            input[1] = 128;
            input[2] = 1;

            byte[] expectedResultBytes = new byte[1 + 1 + input.Length];
            expectedResultBytes[0] = 183 + 1;
            expectedResultBytes[1] = (byte)input.Length;
            expectedResultBytes[2] = input[0];
            expectedResultBytes[3] = input[1];
            expectedResultBytes[4] = input[2];

            Rlp expectedResult = new(expectedResultBytes);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Rlp.Encode(input), Is.EqualTo(expectedResult), "byte array");
                Assert.That(Rlp.Encode(input.AsSpan()), Is.EqualTo(expectedResult), "span");
            }
        }

        [Test]
        public void Long_byte_array()
        {
            byte[] input = new byte[1025];
            input[0] = 255;
            input[1] = 128;
            input[2] = 1;

            byte[] expectedResultBytes = new byte[1 + 2 + input.Length];
            expectedResultBytes[0] = 183 + 2;
            expectedResultBytes[1] = (byte)(input.Length / (16 * 16));
            expectedResultBytes[2] = (byte)(input.Length % (16 * 16));
            expectedResultBytes[3] = input[0];
            expectedResultBytes[4] = input[1];
            expectedResultBytes[5] = input[2];

            Rlp expectedResult = new(expectedResultBytes);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Rlp.Encode(input), Is.EqualTo(expectedResult), "byte array");
                Assert.That(Rlp.Encode(input.AsSpan()), Is.EqualTo(expectedResult), "span");
            }
        }

        [TestCase(new byte[] { 128 }, false)]
        [TestCase(new byte[] { 1 }, true)]
        public void Decode_bool(byte[] rlp, bool expectedBool) =>
            Assert.That(RlpCodec.Read(rlp, static (ref RlpReader r) => r.DecodeBool()), Is.EqualTo(expectedBool));

        [TestCase(new byte[] { 0 })]
        [TestCase(new byte[] { 2 })]
        [TestCase(new byte[] { 3 })]
        [TestCase(new byte[] { 255 })]
        [TestCase(new byte[] { 127 })]
        [TestCase(new byte[] { 129, 1 })]
        [TestCase(new byte[] { 129, 127 })]
        [TestCase(new byte[] { 188, 0 })]
        [TestCase(new byte[] { 184, 55, 1 })]
        [TestCase(new byte[] { 193 })]
        [TestCase(new byte[] { 127, 1, 2, 2 })]
        [TestCase(new byte[] { 130, 1, 0 })]
        [TestCase(new byte[] { 130, 0, 2, 2 })]
        [TestCase(new byte[]
        {184, 56,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0
        })]
        public void Decode_bool_invalid(byte[] rlp) =>
            Assert.Throws<RlpReaderException>(() => RlpCodec.Read(rlp, static (ref RlpReader r) => r.DecodeBool()));

        [Test]
        public void Long_and_big_integer_encoded_the_same(
            [ValueSource(nameof(LongValues))] long value
        )
        {
            Rlp rlpLong = Rlp.Encode(value);

            Rlp rlpBigInt = Rlp.Encode(new BigInteger(value));
            if (value < 0)
            {
                rlpBigInt = Rlp.Encode(new BigInteger(value), 8);
            }

            Assert.That(rlpBigInt.Bytes, Is.EqualTo(rlpLong.Bytes));
        }

        [Test]
        public void Long_using_buffer_encoded_the_same(
            [ValueSource(nameof(LongValues))] long value
        )
        {
            Span<byte> buffer = stackalloc byte[9];
            Span<byte> result = Rlp.Encode(value, buffer);

            Assert.That(result, Is.SequenceEqualTo(Rlp.Encode(value).Bytes));
        }

        [Test]
        public void ULong_using_buffer_encoded_the_same(
            [ValueSource(nameof(ULongValues))] ulong value
        )
        {
            Span<byte> buffer = stackalloc byte[9];
            Span<byte> result = Rlp.Encode(value, buffer);

            Assert.That(result, Is.SequenceEqualTo(Rlp.Encode(value).Bytes));
        }

        [Test]
        public void Encode_generic_with_Rlp_input_preserves_original_bytes()
        {
            Rlp original = Rlp.Encode(255L);
            Rlp reEncoded = Rlp.Encode<Rlp>(original);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(reEncoded.Bytes, Is.EqualTo(original.Bytes));
                Assert.That(reEncoded, Is.SameAs(original));
            }
        }

        [Test]
        public void Over_limit_throws([Values(50, 100)] int limit)
        {
            byte[] rlp = Prepare100BytesRlp();
            RlpLimit rlpLimit = new(limit);
            if (limit < 100)
            {
                Assert.Throws<RlpLimitException>(() => { RlpReader ctx = new(rlp); ctx.DecodeByteArray(rlpLimit); });
            }
            else
            {
                Assert.DoesNotThrow(() => { RlpReader ctx = new(rlp); ctx.DecodeByteArray(rlpLimit); });
            }
        }

        [Test]
        public void Not_enough_bytes_throws()
        {
            byte[] data = Prepare100BytesRlp();
            data[1] = 101; // tamper with length, it is more than available bytes
            Assert.Throws<RlpReaderException>(() => { RlpReader ctx = new(data); ctx.DecodeByteArray(); });
        }

        [Test]
        public void Encode_stream_with_null_items_produces_empty_list()
        {
            byte[] buffer = new byte[Rlp.OfEmptyList.Length];
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            TxDecoder.Instance.Encode(ref writer, (Transaction[]?)null);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(writer.Length, Is.EqualTo(Rlp.OfEmptyList.Length));
                Assert.That(buffer, Is.EqualTo(Rlp.OfEmptyList.Bytes));
            }
        }

        [Test]
        public void Encode_array_with_null_items_produces_empty_list()
        {
            Rlp result = Rlp.Encode<Account>((Account[]?)null!);
            Assert.That(result, Is.EqualTo(Rlp.OfEmptyList));
        }

        private static HashSet<long> LongValues()
        {
            const long minusBit = 1L << 63;
            HashSet<long> seen = [];

            for (int i = 0; i < sizeof(long) * 8; i++)
            {
                long pow2 = 1L << i;

                TryYield(pow2);
                TryYield(pow2 - 1);
                TryYield(pow2 + 1);

                TryYield(pow2 | minusBit);
                TryYield((pow2 - 1) | minusBit);
                TryYield((pow2 + 1) | minusBit);
            }

            return seen;

            void TryYield(long value) => seen.Add(value);
        }

        private static IEnumerable<ulong> ULongValues()
        {
            for (int i = 0; i < sizeof(long) * 8; i++)
            {
                ulong pow2 = 1UL << i;

                yield return pow2;
                yield return pow2 - 1;
                yield return pow2 + 1;
            }

            yield return ulong.MaxValue - 1;
            yield return ulong.MaxValue;
        }

        private static IEnumerable<DecoderCase> IntegerDecoders()
        {
            yield return new(nameof(RlpReaderExtensions.DecodeByte), static ctx => ctx.DecodeByte(), sizeof(byte));
            yield return new(nameof(RlpReaderExtensions.DecodeUInt), static ctx => ctx.DecodeUInt(), sizeof(uint));
            yield return new(nameof(RlpReaderExtensions.DecodeInt), static ctx => ctx.DecodeInt(), sizeof(int));
            yield return new(nameof(RlpReaderExtensions.DecodeULong), static ctx => ctx.DecodeULong(), sizeof(ulong));
            yield return new(nameof(RlpReaderExtensions.DecodeLong), static ctx => ctx.DecodeLong(), sizeof(long));
            yield return new(nameof(RlpReaderExtensions.DecodeUInt256), static ctx => ctx.DecodeUInt256(), 256 / 8);
        }

        private static (byte[] rlp, string expectedHex)[] IntegerTestCases() =>
        [
            ([0x05], "5"),
            ([0x80], "0"),
            ([0x81, 0x80], "80"),
            ([0x81, 0x81], "81"),
            ([0x81, 0xFF], "FF"),
            ([0x82, 0x05, 0x05], "505"),
            ([0x82, 0xFF, 0xFF], "FFFF"),
            ([0x83, 0x05, 0x05, 0x05], "50505"),
            ([0x84, 0x05, 0x05, 0x05, 0x05], "5050505"),
            ([0x84, 0xFF, 0xFF, 0xFF, 0xFF], "FFFFFFFF"),
            ([0x88, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05], "505050505050505"),
            ([0x88, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], "FFFFFFFFFFFFFFFF"),
            ([0x8C, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05], "50505050505050505050505"),
            ([0x8C, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], "FFFFFFFFFFFFFFFFFFFFFFFF"),
        ];

        private static byte[] Prepare100BytesRlp()
        {
            byte[] randomBytes = new byte[100];
            Random.Shared.NextBytes(randomBytes);

            int requiredLength = Rlp.LengthOf(randomBytes);
            byte[] rlp = new byte[requiredLength];
            RlpWriter writer = RlpWriter.ContentWriter(rlp);
            writer.Encode(randomBytes);
            return rlp;
        }

        [Test]
        public void Item_length_of_all_short_form_prefixes()
        {
            // Prefix 0-127: single byte, total = 1
            // Prefix 128-183: short string, total = 1 + (prefix - 128)
            // Prefix 192-247: short list, total = 1 + (prefix - 192)
            for (int prefix = 0; prefix <= 247; prefix++)
            {
                if (prefix >= 184 && prefix <= 191) continue;

                int expected = prefix < 128 ? 1
                    : prefix <= 183 ? 1 + (prefix - 128)
                    : 1 + (prefix - 192);

                byte[] data = new byte[Math.Max(expected, 1)];
                data[0] = (byte)prefix;

                Assert.That(ItemLength(data), Is.EqualTo(expected), $"prefix {prefix}");
            }
        }

        [TestCase(184, 56)]   // long string, lengthOfLength=1
        [TestCase(185, 256)]  // long string, lengthOfLength=2
        [TestCase(248, 56)]   // long list, lengthOfLength=1
        [TestCase(249, 256)]  // long list, lengthOfLength=2
        public void Item_length_of_long_form(int prefix, int contentLength)
        {
            byte[] data = BuildLongFormRlp(prefix, contentLength);

            Assert.That(ItemLength(data), Is.EqualTo(data.Length), $"prefix {prefix}");
        }

        [TestCase(0, 0, 1)]       // single byte: prefix=0, content=1
        [TestCase(127, 0, 1)]     // single byte: prefix=127, content=1
        [TestCase(128, 1, 0)]     // short string: empty
        [TestCase(129, 1, 1)]     // short string: 1 byte content
        [TestCase(183, 1, 55)]    // short string: max short (55 bytes)
        [TestCase(192, 1, 0)]     // short list: empty
        [TestCase(193, 1, 1)]     // short list: 1 byte content
        [TestCase(247, 1, 55)]    // short list: max short (55 bytes)
        public void Prefix_and_content_length_short_form(int prefix, int expectedPrefixLen, int expectedContentLen)
        {
            byte[] data = new byte[1 + expectedContentLen];
            data[0] = (byte)prefix;

            Assert.That(RlpTestHelper.PrefixAndContentLength(data), Is.EqualTo((expectedPrefixLen, expectedContentLen)), $"prefix {prefix}");
        }

        [TestCase(184, 56)]   // long string, lengthOfLength=1
        [TestCase(248, 56)]   // long list, lengthOfLength=1
        public void Prefix_and_content_length_long_form(int prefix, int contentLength)
        {
            int lengthOfLength = prefix < 192 ? prefix - 183 : prefix - 247;
            byte[] data = BuildLongFormRlp(prefix, contentLength);

            Assert.That(RlpTestHelper.PrefixAndContentLength(data), Is.EqualTo((1 + lengthOfLength, contentLength)), $"prefix {prefix}");
        }

        [TestCase(new byte[] { 0xBB, 0x7F, 0xFF, 0xFF, 0xFF }, TestName = "LongString_4ByteLength_Int32Max")]
        [TestCase(new byte[] { 0xFB, 0x7F, 0xFF, 0xFF, 0xFF }, TestName = "LongList_4ByteLength_Int32Max")]
        [TestCase(new byte[] { 0xB8, 0x64, 0x01, 0x02 }, TestName = "LongString_1ByteLength_100")]
        [TestCase(new byte[] { 0xF8, 0x64, 0x01, 0x02 }, TestName = "LongList_1ByteLength_100")]
        public void Prefix_and_content_length_invalid(byte[] data) =>
            Assert.Throws<RlpReaderException>(() => RlpTestHelper.PrefixAndContentLength(data));

        [Test]
        public void CountItems_mixed_items()
        {
            // [singleByte, shortString(2), emptyString, shortList(1), singleByte] = 5 items
            byte[] rlp = [0xC8, 0x42, 0x82, 0xAB, 0xCD, 0x80, 0xC1, 0x05, 0x7F];
            AssertItemCount(rlp, 5);
        }

        [Test]
        public void CountItems_with_nested_lists()
        {
            // [0x01, [0x02, 0x03], 0x04] = 3 items
            byte[] rlp = [0xC5, 0x01, 0xC2, 0x02, 0x03, 0x04];
            AssertItemCount(rlp, 3);
        }

        [Test]
        public void CountItems_with_long_string()
        {
            // [singleByte, longString(56 bytes), singleByte] = 3 items
            byte[] items = new byte[60];
            items[0] = 0x42;
            items[1] = 184;
            items[2] = 56;
            items[3] = 0xFF; // non-zero first byte of long content
            items[59] = 0x43;

            byte[] rlp = new byte[2 + items.Length];
            rlp[0] = 248;
            rlp[1] = 60;
            items.CopyTo(rlp.AsSpan(2));

            AssertItemCount(rlp, 3);
        }

        [TestCase(2, 56)]
        [TestCase(2, 198)]
        [TestCase(3, 256)]
        [TestCase(3, 65535)]
        [TestCase(4, 65536)]
        public void Prefix_and_content_length_of_list(int prefixLength, int contentLength)
        {
            byte[] data = Rlp.Encode(Rlp.Encode(new byte[contentLength])).Bytes;

            Assert.That(RlpTestHelper.PrefixAndContentLength(data), Is.EqualTo((prefixLength, contentLength)));
        }

        [TestCase(2, 56)]
        [TestCase(2, 255)]
        [TestCase(3, 256)]
        [TestCase(4, 65536)]
        public void Prefix_and_content_length_of_string(int prefixLength, int contentLength)
        {
            byte[] data = Rlp.Encode(new byte[contentLength]).Bytes;

            Assert.That(RlpTestHelper.PrefixAndContentLength(data), Is.EqualTo((prefixLength, contentLength)));
        }

        /// <summary>Gets the length of the first item, without the checks of the canonical form.</summary>
        private static int ItemLength(ReadOnlySpan<byte> data)
        {
            RlpReader reader = new(data);
            return reader.ReadEncodedItem<OffFlag>().Length;
        }

        private static byte[] ExpectedValueHash(ValueHash256? value)
        {
            if (value is null)
            {
                return [128];
            }

            byte[] expected = new byte[Rlp.LengthOf(in value)];
            expected[0] = 160;
            value.Value.Bytes.CopyTo(expected.AsSpan(1));
            return expected;
        }

        private static byte[] ExpectedAddress(Address? value)
        {
            if (value is null)
            {
                return [128];
            }

            byte[] expected = new byte[Rlp.LengthOf(value)];
            expected[0] = 148;
            value.Bytes.CopyTo(expected.AsSpan(1));
            return expected;
        }

        private static byte[] ExpectedBloom(Bloom? value)
        {
            if (value is null)
            {
                return [128];
            }

            byte[] expected = new byte[Rlp.LengthOf(value)];
            expected[0] = 185;
            expected[1] = 1;
            expected[2] = 0;
            value.Bytes.CopyTo(expected.AsSpan(3));
            return expected;
        }

        private static void AssertValueWriterMatchesExpected(RlpWriter writer, ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> expected)
        {
            Assert.That(writer.Length, Is.EqualTo(expected.Length));
            Assert.That(buffer[..writer.Length], Is.SequenceEqualTo(expected));
        }

        private static IEnumerable<byte[]> ValueWriterByteArrayCases()
        {
            yield return [];
            yield return [0];
            yield return [1];
            yield return [127];
            yield return [128];
            yield return Enumerable.Repeat((byte)0xab, 55).ToArray();
            yield return Enumerable.Repeat((byte)0xab, 56).ToArray();
        }

        private static IEnumerable<UInt256> ValueWriterUInt256Cases()
        {
            yield return UInt256.Zero;
            yield return 127;
            yield return 128;
            yield return UInt256.MaxValue;
        }

        private static IEnumerable<Hash256?> ValueWriterHashCases()
        {
            yield return null;
            yield return Keccak.OfAnEmptyString;
            yield return Keccak.EmptyTreeHash;
            yield return new Hash256(Enumerable.Range(0, Hash256.Size).Select(static i => (byte)i).ToArray());
        }

        private static IEnumerable<ValueHash256?> ValueWriterValueHashCases()
        {
            yield return null;
            yield return Keccak.OfAnEmptyString.ValueHash256;
            yield return Keccak.EmptyTreeHash.ValueHash256;
            yield return new ValueHash256(Enumerable.Range(0, Hash256.Size).Select(static i => (byte)i).ToArray());
        }

        private static IEnumerable<Address?> ValueWriterAddressCases()
        {
            yield return null;
            yield return Address.Zero;
            yield return new Address(Enumerable.Range(0, Address.Size).Select(static i => (byte)i).ToArray());
        }

        private static IEnumerable<Bloom?> ValueWriterBloomCases()
        {
            yield return null;
            yield return Bloom.Empty;
            yield return new Bloom(Enumerable.Range(0, Bloom.ByteLength).Select(static i => (byte)i).ToArray());
        }

        private static IEnumerable<byte[][]> ValueWriterByteArraySequenceCases()
        {
            yield return [];
            yield return [[1], [128], Enumerable.Repeat((byte)0xab, 56).ToArray()];
        }

        private static void AssertItemCount(byte[] rlp, int expected)
        {
            RlpReader reader = new(rlp);
            Assert.That(reader.EnterSequence(out _).CountItems(), Is.EqualTo(expected));
        }

        [TestCase(184, 10)]  // long string with content < 56
        [TestCase(184, 55)]  // long string with content = 55 (max short-form)
        [TestCase(248, 10)]  // long list with content < 56
        [TestCase(248, 55)]  // long list with content = 55 (max short-form)
        public void NonCanonical_long_form_rejected(int prefix, int contentLength)
        {
            byte[] data = BuildLongFormRlp(prefix, contentLength);

            Assert.Throws<RlpReaderException>(() =>
            {
                RlpReader reader = new(data);
                reader.SkipItem();
            });

            using IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(owner.Memory.Span);
            Assert.Throws<RlpReaderException>(() => new RlpItemList(owner, owner.Memory[..data.Length]));
        }

        [Test]
        public void RlpReader_from_default_capped_array_is_empty()
        {
            CappedArray<byte> data = default;
            RlpReader reader = new(data.AsSpan());

            Assert.That(reader.HasNext, Is.False);
        }

        [Test]
        public void SkipItems_advances_the_cursor_like_repeated_SkipItem([Range(-1, MixedItemCount)] int count)
        {
            byte[] rlp = MixedRlpItems();

            RlpReader batched = new(rlp);
            batched.SkipItems(count);

            RlpReader oneByOne = new(rlp);
            for (int i = 0; i < count; i++)
            {
                oneByOne.SkipItem();
            }

            Assert.That(batched.BytesRead, Is.EqualTo(oneByOne.BytesRead));
        }

        [Test]
        public void SkipItems_over_every_item_lands_at_the_end()
        {
            byte[] rlp = MixedRlpItems();
            RlpReader reader = new(rlp);

            reader.SkipItems(MixedItemCount);

            Assert.That(reader.BytesRead, Is.EqualTo(rlp.Length));
        }

        private const int MixedItemCount = 5;

        // 0x7F | "" | "abc" | [1, 2] | a 56-byte string, so a run crosses every prefix form
        private static byte[] MixedRlpItems() =>
            [0x7F, 0x80, 0x83, 0x61, 0x62, 0x63, 0xC2, 0x01, 0x02, 0xB8, 0x38, .. new byte[56]];

        private static byte[] BuildLongFormRlp(int prefix, int contentLength)
        {
            int lengthOfLength = prefix < 192 ? prefix - 183 : prefix - 247;
            byte[] data = new byte[1 + lengthOfLength + contentLength];
            data[0] = (byte)prefix;
            int remaining = contentLength;
            for (int i = lengthOfLength; i >= 1; i--)
            {
                data[i] = (byte)(remaining & 0xFF);
                remaining >>= 8;
            }
            return data;
        }

        [TestCase(new byte[] { 0xB8 }, Description = "Long string prefix 0xB8 (1 byte of length), but no length byte")]
        [TestCase(new byte[] { 0xB9, 0x01 }, Description = "Long string prefix 0xB9 (2 bytes of length), but only 1 length byte")]
        [TestCase(new byte[] { 0xBB, 0x00, 0x01 }, Description = "Long string prefix 0xBB (4 bytes of length), but only 2 length bytes")]
        public void SkipItem_throws_on_truncated_long_length(byte[] truncatedData)
        {
            // These prefixes declare a multi-byte length field, but the data is truncated
            // before all length bytes are present. The bounds check should catch this.
            Action act = () => { RlpReader reader = new(truncatedData); reader.SkipItem(); };
            Assert.That(act, Throws.TypeOf<RlpReaderException>());
        }

        [Test]
        [NonParallelizable]
        public unsafe void SkipItem_throws_on_truncated_length_ending_at_int_max([Values(0xB8, 0xF8)] byte prefix)
        {
            // The span's last byte is the prefix, so position + 1 + lengthOfLength wraps an int. The native
            // block holds the byte one past the span's end, which an unchecked read would decode as a short
            // non-canonical length instead of reporting truncation. Only its last page is ever touched.
            const int position = int.MaxValue - 1;
            byte* backing;
            try
            {
                backing = (byte*)NativeMemory.Alloc((nuint)int.MaxValue + 1);
            }
            catch (OutOfMemoryException)
            {
                Assert.Ignore("Cannot allocate the 2 GB native block this test needs.");
                return;
            }

            try
            {
                backing[position] = prefix;
                backing[int.MaxValue] = 0x01;

                Action act = () => { RlpReader reader = new(new ReadOnlySpan<byte>(backing, int.MaxValue)); reader.ReadRaw(position); reader.SkipItem(); };

                Assert.That(act, Throws.TypeOf<RlpReaderException>().With.Message.Contains("truncated"));
            }
            finally
            {
                NativeMemory.Free(backing);
            }
        }
    }
}
