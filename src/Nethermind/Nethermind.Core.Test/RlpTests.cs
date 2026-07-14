// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
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
            Assert.That(output[..written].ToArray(), Is.EqualTo(expected));
        }

        [TestCaseSource(nameof(ValueWriterByteArrayCases))]
        public void RlpWriter_encodes_byte_spans_like_Rlp(byte[] value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = new(buffer);
            writer.Encode((ReadOnlySpan<byte>)value);

            AssertValueWriterMatchesExpected(writer, buffer, Rlp.Encode((ReadOnlySpan<byte>)value).Bytes);
        }

        [TestCase(0UL)]
        [TestCase(1UL)]
        [TestCase(127UL)]
        [TestCase(128UL)]
        [TestCase(255UL)]
        [TestCase(256UL)]
        [TestCase(ulong.MaxValue)]
        public void RlpWriter_encodes_ulong_like_Rlp(ulong value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = new(buffer);
            writer.Encode(value);

            Span<byte> expected = stackalloc byte[9];
            expected = Rlp.Encode(value, expected);
            AssertValueWriterMatchesExpected(writer, buffer, expected);
        }

        [TestCaseSource(nameof(ValueWriterUInt256Cases))]
        public void RlpWriter_encodes_uint256_like_Rlp(UInt256 value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = new(buffer);
            writer.Encode(in value);

            AssertValueWriterMatchesExpected(writer, buffer, Rlp.Encode(in value).Bytes);
        }

        [TestCaseSource(nameof(ValueWriterHashCases))]
        public void RlpWriter_encodes_hash_like_Rlp(Hash256? value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = new(buffer);
            writer.Encode(value);

            AssertValueWriterMatchesExpected(writer, buffer, Rlp.Encode(value).Bytes);
        }

        [TestCaseSource(nameof(ValueWriterValueHashCases))]
        public void RlpWriter_encodes_value_hash_like_Rlp(ValueHash256? value)
        {
            int length = Rlp.LengthOf(in value);

            byte[] buffer = new byte[length];
            RlpWriter writer = new(buffer);
            writer.Encode(in value);

            AssertValueWriterMatchesExpected(writer, buffer, ExpectedValueHash(value));
        }

        [TestCaseSource(nameof(ValueWriterAddressCases))]
        public void RlpWriter_encodes_address_like_Rlp(Address? value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = new(buffer);
            writer.Encode(value);

            AssertValueWriterMatchesExpected(writer, buffer, ExpectedAddress(value));
        }

        [TestCaseSource(nameof(ValueWriterBloomCases))]
        public void RlpWriter_encodes_bloom_like_Rlp(Bloom? value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = new(buffer);
            writer.Encode(value);

            AssertValueWriterMatchesExpected(writer, buffer, ExpectedBloom(value));
        }

        [TestCaseSource(nameof(ValueWriterByteArraySequenceCases))]
        public void RlpWriter_encodes_byte_array_sequences_like_Rlp(byte[][] value)
        {
            int length = Rlp.LengthOf(value);

            byte[] buffer = new byte[length];
            RlpWriter writer = new(buffer);
            writer.Encode(value);

            AssertValueWriterMatchesExpected(writer, buffer, Rlp.Encode(value.Select(Rlp.Encode).ToArray()).Bytes);
        }

        [Test]
        public void RlpWriter_extensions_encode_directly_to_custom_backend()
        {
            TestRlpWriteBackend writer = new();
            writer.Encode(Bloom.Empty);

            Assert.That(writer.Bytes.ToArray(), Is.EqualTo(ExpectedBloom(Bloom.Empty)));
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
            RlpReader context = new(rlp);
            context.Position = position;

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
                Throws.InstanceOf<RlpException>()
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
                new byte[] { 0x83, 0x00, 0x01, 0x00 }, new byte[] { 0x84, 0x00, 0x00, 0x00, 0x01 }
            )] byte[] bytes) =>
            Assert.That(
                () => decoder.Invoke(new RlpReader(bytes)),
                Throws.InstanceOf<RlpException>().Or.InstanceOf<IndexOutOfRangeException>()
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
        public void Length_of_ulong_same_as_uint256([ValueSource(nameof(ULongValues))] ulong value) => Assert.That(Rlp.LengthOf(value), Is.EqualTo(Rlp.LengthOf((UInt256)value)));

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

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(127)]
        public void Byte_array_of_length_1_and_first_byte_value_less_than_128(byte value)
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

        [TestCase(128)]
        [TestCase(255)]
        public void Byte_array_of_length_1_and_first_byte_value_equal_or_more_than_128(byte value)
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
            Assert.That(new RlpReader(rlp).DecodeBool(), Is.EqualTo(expectedBool));

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
            Assert.Throws<RlpException>(() => new RlpReader(rlp).DecodeBool());

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

            Assert.That(result.ToArray(), Is.EqualTo(Rlp.Encode(value).Bytes));
        }

        [Test]
        public void ULong_using_buffer_encoded_the_same(
            [ValueSource(nameof(ULongValues))] ulong value
        )
        {
            Span<byte> buffer = stackalloc byte[9];
            Span<byte> result = Rlp.Encode(value, buffer);

            Assert.That(result.ToArray(), Is.EqualTo(Rlp.Encode(value).Bytes));
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

        [TestCase(50)]
        [TestCase(100)]
        public void Over_limit_throws(int limit)
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
            Assert.Throws<RlpLimitException>(() => { RlpReader ctx = new(data); ctx.DecodeByteArray(); });
        }

        [Test]
        public void Encode_stream_with_null_items_produces_empty_list()
        {
            byte[] buffer = new byte[Rlp.OfEmptyList.Length];
            RlpWriter writer = new(buffer);
            TxDecoder.Instance.Encode(ref writer, (Transaction[]?)null);

            Assert.That(writer.Position, Is.EqualTo(Rlp.OfEmptyList.Length));
            Assert.That(buffer, Is.EqualTo(Rlp.OfEmptyList.Bytes));
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
            yield return new(nameof(RlpReader.DecodeByte), static ctx => ctx.DecodeByte(), sizeof(byte));
            yield return new(nameof(RlpReader.DecodeUShort), static ctx => ctx.DecodeUShort(), sizeof(ushort));
            yield return new(nameof(RlpReader.DecodeUInt), static ctx => ctx.DecodeUInt(), sizeof(uint));
            yield return new(nameof(RlpReader.DecodeInt), static ctx => ctx.DecodeInt(), sizeof(int));
            yield return new(nameof(RlpReader.DecodeULong), static ctx => ctx.DecodeULong(), sizeof(ulong));
            yield return new(nameof(RlpReader.DecodeLong), static ctx => ctx.DecodeLong(), sizeof(long));
            yield return new(nameof(RlpReader.DecodeUInt256), static ctx => ctx.DecodeUInt256(), 256 / 8);
            yield return new(nameof(RlpReader.DecodeUBigInt), static ctx => ctx.DecodeUBigInt(), null);
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
            RlpWriter writer = new(rlp);
            writer.Encode(randomBytes);
            return rlp;
        }

        [Test]
        public void PeekNextRlpLength_all_short_form_prefixes()
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

                RlpReader ctx = new(data);
                Assert.That(ctx.PeekNextRlpLength(), Is.EqualTo(expected), $"RlpReader prefix {prefix}");

                CappedArray<byte> cappedData = new(data);
                RlpReader cappedReader = new(cappedData);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(cappedReader.IsNotNull, Is.True);
                    Assert.That(cappedReader.PeekNextRlpLength(), Is.EqualTo(expected), $"RlpReader capped prefix {prefix}");
                }
            }
        }

        [TestCase(184, 56)]   // long string, lengthOfLength=1
        [TestCase(185, 256)]  // long string, lengthOfLength=2
        [TestCase(248, 56)]   // long list, lengthOfLength=1
        [TestCase(249, 256)]  // long list, lengthOfLength=2
        public void PeekNextRlpLength_long_form(int prefix, int contentLength)
        {
            byte[] data = BuildLongFormRlp(prefix, contentLength);

            RlpReader ctx = new(data);
            Assert.That(ctx.PeekNextRlpLength(), Is.EqualTo(data.Length), $"RlpReader prefix {prefix}");

            CappedArray<byte> cappedData = new(data);
            RlpReader cappedReader = new(cappedData);
            Assert.That(cappedReader.PeekNextRlpLength(), Is.EqualTo(data.Length), $"RlpReader capped prefix {prefix}");
        }

        [TestCase(0, 0, 1)]       // single byte: prefix=0, content=1
        [TestCase(127, 0, 1)]     // single byte: prefix=127, content=1
        [TestCase(128, 1, 0)]     // short string: empty
        [TestCase(129, 1, 1)]     // short string: 1 byte content
        [TestCase(183, 1, 55)]    // short string: max short (55 bytes)
        [TestCase(192, 1, 0)]     // short list: empty
        [TestCase(193, 1, 1)]     // short list: 1 byte content
        [TestCase(247, 1, 55)]    // short list: max short (55 bytes)
        public void PeekPrefixAndContentLength_short_form(int prefix, int expectedPrefixLen, int expectedContentLen)
        {
            byte[] data = new byte[1 + expectedContentLen];
            data[0] = (byte)prefix;

            RlpReader ctx = new(data);
            (int pLen, int cLen) = ctx.PeekPrefixAndContentLength();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pLen, Is.EqualTo(expectedPrefixLen), $"RlpReader prefix length for {prefix}");
                Assert.That(cLen, Is.EqualTo(expectedContentLen), $"RlpReader content length for {prefix}");
            }

            CappedArray<byte> cappedData = new(data);
            RlpReader cappedReader = new(cappedData);
            (int pLen2, int cLen2) = cappedReader.PeekPrefixAndContentLength();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pLen2, Is.EqualTo(expectedPrefixLen), $"RlpReader capped prefix length for {prefix}");
                Assert.That(cLen2, Is.EqualTo(expectedContentLen), $"RlpReader capped content length for {prefix}");
            }
        }

        [TestCase(184, 56)]   // long string, lengthOfLength=1
        [TestCase(248, 56)]   // long list, lengthOfLength=1
        public void PeekPrefixAndContentLength_long_form(int prefix, int contentLength)
        {
            int lengthOfLength = prefix < 192 ? prefix - 183 : prefix - 247;
            byte[] data = BuildLongFormRlp(prefix, contentLength);

            RlpReader ctx = new(data);
            (int pLen, int cLen) = ctx.PeekPrefixAndContentLength();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pLen, Is.EqualTo(1 + lengthOfLength), $"RlpReader prefix length for {prefix}");
                Assert.That(cLen, Is.EqualTo(contentLength), $"RlpReader content length for {prefix}");
            }

            CappedArray<byte> cappedData = new(data);
            RlpReader cappedReader = new(cappedData);
            (int pLen2, int cLen2) = cappedReader.PeekPrefixAndContentLength();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pLen2, Is.EqualTo(1 + lengthOfLength), $"RlpReader capped prefix length for {prefix}");
                Assert.That(cLen2, Is.EqualTo(contentLength), $"RlpReader capped content length for {prefix}");
            }
        }

        [TestCase(new byte[] { 0xBB, 0x7F, 0xFF, 0xFF, 0xFF }, TestName = "LongString_4ByteLength_Int32Max")]
        [TestCase(new byte[] { 0xFB, 0x7F, 0xFF, 0xFF, 0xFF }, TestName = "LongList_4ByteLength_Int32Max")]
        [TestCase(new byte[] { 0xB8, 0x64, 0x01, 0x02 }, TestName = "LongString_1ByteLength_100")]
        [TestCase(new byte[] { 0xF8, 0x64, 0x01, 0x02 }, TestName = "LongList_1ByteLength_100")]
        public void PeekPrefixAndContentLength_invalid(byte[] data) =>
            Assert.Throws<RlpException>(() =>
            {
                RlpReader ctx = new(data);
                ctx.PeekPrefixAndContentLength();
            });

        [Test]
        public void PeekNumberOfItemsRemaining_mixed_items()
        {
            // [singleByte, shortString(2), emptyString, shortList(1), singleByte] = 5 items
            byte[] rlp = [0xC8, 0x42, 0x82, 0xAB, 0xCD, 0x80, 0xC1, 0x05, 0x7F];
            AssertItemCount(rlp, 5);
        }

        [Test]
        public void PeekNumberOfItemsRemaining_with_nested_lists()
        {
            // [0x01, [0x02, 0x03], 0x04] = 3 items
            byte[] rlp = [0xC5, 0x01, 0xC2, 0x02, 0x03, 0x04];
            AssertItemCount(rlp, 3);
        }

        [Test]
        public void PeekNumberOfItemsRemaining_with_long_string()
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
        public void ReadPrefixAndContentLength_List(int prefixLength, int contentLength)
        {
            byte[] data = Rlp.Encode(Rlp.Encode(new byte[contentLength])).Bytes;

            RlpReader ctx = new(data.AsSpan());
            Assert.That(ctx.ReadPrefixAndContentLength(), Is.EqualTo((prefixLength, contentLength)));
            Assert.That(ctx.Position, Is.EqualTo(prefixLength));
        }

        [TestCase(2, 56)]
        [TestCase(2, 255)]
        [TestCase(3, 256)]
        [TestCase(4, 65536)]
        public void ReadPrefixAndContentLength_String(int prefixLength, int contentLength)
        {
            byte[] data = Rlp.Encode(new byte[contentLength]).Bytes;

            RlpReader ctx = new(data.AsSpan());
            Assert.That(ctx.ReadPrefixAndContentLength(), Is.EqualTo((prefixLength, contentLength)));
            Assert.That(ctx.Position, Is.EqualTo(prefixLength));
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
            Assert.That(writer.Position, Is.EqualTo(expected.Length));
            Assert.That(buffer[..writer.Position].ToArray(), Is.EqualTo(expected.ToArray()));
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

        private struct TestRlpWriteBackend : IRlpWriteBackend
        {
            public TestRlpWriteBackend() => Bytes = [];

            public List<byte> Bytes { get; }

            public void WriteByte(byte byteToWrite) => Bytes.Add(byteToWrite);

            public void Write(ReadOnlySpan<byte> bytesToWrite)
            {
                for (int i = 0; i < bytesToWrite.Length; i++)
                {
                    Bytes.Add(bytesToWrite[i]);
                }
            }

            public void WriteZero(int length)
            {
                for (int i = 0; i < length; i++)
                {
                    Bytes.Add(0);
                }
            }

        }

        private static void AssertItemCount(byte[] rlp, int expected)
        {
            RlpReader ctx = new(rlp);
            ctx.ReadSequenceLength();
            Assert.That(ctx.PeekNumberOfItemsRemaining(), Is.EqualTo(expected));

            CappedArray<byte> cappedRlp = new(rlp);
            RlpReader cappedReader = new(cappedRlp);
            cappedReader.ReadSequenceLength();
            Assert.That(cappedReader.PeekNumberOfItemsRemaining(), Is.EqualTo(expected));
        }

        [TestCase(184, 10)]  // long string with content < 56
        [TestCase(184, 55)]  // long string with content = 55 (max short-form)
        [TestCase(248, 10)]  // long list with content < 56
        [TestCase(248, 55)]  // long list with content = 55 (max short-form)
        public void NonCanonical_long_form_rejected(int prefix, int contentLength)
        {
            byte[] data = BuildLongFormRlp(prefix, contentLength);
            AssertNonCanonicalThrows(data);
        }

        private static void AssertNonCanonicalThrows(byte[] data)
        {
            // Ref structs cannot be captured in lambdas, so use try/catch instead of Assert.Throws
            try
            {
                RlpReader ctx = new(data);
                ctx.PeekPrefixAndContentLength();
                Assert.Fail("Expected RlpException from RlpReader");
            }
            catch (RlpException) { }

            try
            {
                CappedArray<byte> cappedData = new(data);
                RlpReader cappedReader = new(cappedData);
                cappedReader.PeekPrefixAndContentLength();
                Assert.Fail("Expected RlpException from capped RlpReader");
            }
            catch (RlpException) { }

            using IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(owner.Memory.Span);
            Assert.Throws<RlpException>(() => new RlpItemList(owner, owner.Memory[..data.Length]));
        }

        [Test]
        public void RlpReader_from_default_capped_array_is_null()
        {
            CappedArray<byte> data = default;
            RlpReader reader = new(data);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.IsNull, Is.True);
                Assert.That(reader.IsNotNull, Is.False);
                Assert.That(reader.Length, Is.Zero);
            }
        }

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
        public void PeekLongPrefixAndContentLength_throws_on_truncated_data(byte[] truncatedData)
        {
            // These prefixes declare a multi-byte length field, but the data is truncated
            // before all length bytes are present. The bounds check should catch this.
            Action act = () => RlpHelpers.PeekNextRlpLength(truncatedData, 0);
            Assert.That(act, Throws.TypeOf<RlpException>());
        }
    }
}
