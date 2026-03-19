// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class RlpTests
    {
        public record DecoderCase(string Name, Action<Rlp.ValueDecoderContext> Invoke, int? Size)
        {
            public override string ToString() => Name;
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

        [Test]
        [Explicit("That was a regression test but now it is failing again and cannot find the reason we needed this behaviour in the first place. Sync works all fine. Leaving it here as it may resurface - make sure to add more explanation to it in such case.")]
        public void Serializing_object_int_regression()
        {
            Rlp output = Rlp.Encode(new[] { Rlp.Encode(1) });
            Assert.That(output.Bytes, Is.EqualTo(new byte[] { 1 }));
        }

        [TestCase(new byte[] { 0x05 }, 5)]
        [TestCase(new byte[] { 0x80 }, 0)]
        [TestCase(new byte[] { 0x81, 0x80 }, 0x80)]
        [TestCase(new byte[] { 0x81, 0x81 }, 0x81)]
        [TestCase(new byte[] { 0x81, 0xFF }, byte.MaxValue)]
        public void Decode_byte(byte[] rlp, byte value)
        {
            Assert.That(rlp.AsRlpValueContext().DecodeByte(), Is.EqualTo(value));
        }

        [TestCase(new byte[] { 0x05 }, (ushort)5)]
        [TestCase(new byte[] { 0x80 }, (ushort)0)]
        [TestCase(new byte[] { 0x81, 0x80 }, (ushort)0x80)]
        [TestCase(new byte[] { 0x81, 0x81 }, (ushort)0x81)]
        [TestCase(new byte[] { 0x82, 0x05, 0x05 }, (ushort)0x0505)]
        [TestCase(new byte[] { 0x82, 0xFF, 0xFF }, ushort.MaxValue)]
        public void Decode_ushort(byte[] rlp, ushort value)
        {
            Assert.That(rlp.AsRlpValueContext().DecodeUShort(), Is.EqualTo(value));
        }

        [TestCase(new byte[] { 0x05 }, 5U)]
        [TestCase(new byte[] { 0x80 }, 0U)]
        [TestCase(new byte[] { 0x81, 0x80 }, 0x80U)]
        [TestCase(new byte[] { 0x81, 0x81 }, 0x81U)]
        [TestCase(new byte[] { 0x82, 0x05, 0x05 }, 0x0505U)]
        [TestCase(new byte[] { 0x83, 0x05, 0x05, 0x05 }, 0x050505U)]
        [TestCase(new byte[] { 0x84, 0x05, 0x05, 0x05, 0x05 }, 0x05050505U)]
        [TestCase(new byte[] { 0x84, 0xFF, 0xFF, 0xFF, 0xFF }, uint.MaxValue)]
        public void Decode_uint(byte[] rlp, uint value)
        {
            Assert.That(rlp.AsRlpValueContext().DecodeUInt(), Is.EqualTo(value));
        }

        [TestCase(new byte[] { 0x05 }, 5UL)]
        [TestCase(new byte[] { 0x80 }, 0UL)]
        [TestCase(new byte[] { 0x81, 0x80 }, 0x80UL)]
        [TestCase(new byte[] { 0x81, 0x81 }, 0x81UL)]
        [TestCase(new byte[] { 0x82, 0x05, 0x05 }, 0x0505UL)]
        [TestCase(new byte[] { 0x88, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05 }, 0x0505050505050505UL)]
        [TestCase(new byte[] { 0x88, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, ulong.MaxValue)]
        public void Decode_ulong(byte[] rlp, ulong value)
        {
            Assert.That(rlp.AsRlpValueContext().DecodeULong(), Is.EqualTo(value));
        }

        [TestCase(new byte[] { 0x05 }, 5L)]
        [TestCase(new byte[] { 0x80 }, 0L)]
        [TestCase(new byte[] { 0x81, 0x81 }, 0x81L)]
        [TestCase(new byte[] { 0x82, 0x05, 0x05 }, 0x0505L)]
        [TestCase(new byte[] { 0x88, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05 }, 0x0505050505050505L)]
        [TestCase(new byte[] { 0x88, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, -1L)]
        [TestCase(new byte[] { 0x88, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, long.MaxValue)]
        [TestCase(new byte[] { 0x88, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, long.MinValue)]
        public void Decode_long(byte[] rlp, long value)
        {
            Assert.That(rlp.AsRlpValueContext().DecodeLong(), Is.EqualTo(value));
        }

        [TestCaseSource(nameof(IntegerDecoders))]
        public void Decode_integer_over_size_limit(DecoderCase decoder)
        {
            if (decoder.Size is not { } size)
            {
                Assert.Ignore("No size limit");
                return;
            }

            byte[] bytes = Enumerable.Repeat<byte>(0x99, size + 2).ToArray();
            bytes[0] = (byte)(0x80 + size + 1);

            Assert.That(
                () => decoder.Invoke(bytes.AsRlpValueContext()),
                Throws.TypeOf<RlpException>().Or.TypeOf<RlpLimitException>()
            );
        }

        [Test]
        public void Decode_integer_non_canonical_single_byte(
            [ValueSource(nameof(IntegerDecoders))] DecoderCase decoder,
            [Values(
                new byte[] { 0x81, 0x00 }, new byte[] { 0x81, 0x01 }, new byte[] { 0x81, 0x0F },
                new byte[] { 0x81, 0x10 }, new byte[] { 0x81, 0x1F }, new byte[] { 0x81, 0x7F },
                new byte[] { 0x82, 0x00, 0x81 }, new byte[] { 0x82, 0x00, 0xFF },
                new byte[] { 0x83, 0x00, 0x01, 0x00 }, new byte[] { 0x84, 0x00, 0x00, 0x00, 0x01 }
            )] byte[] bytes)
        {
            Assert.Throws<RlpException>(() => decoder.Invoke(bytes.AsRlpValueContext()));
        }

        [Test]
        public void Length_of_uint()
        {
            Assert.That(Rlp.LengthOf(UInt256.Zero), Is.EqualTo(1));
            Assert.That(Rlp.LengthOf((UInt256)127), Is.EqualTo(1));
            Assert.That(Rlp.LengthOf((UInt256)128), Is.EqualTo(2));

            UInt256 item = 255;
            for (int i = 0; i < 32; i++)
            {
                Assert.That(Rlp.LengthOf(item), Is.EqualTo(i + 2));
                item *= 256;
            }
        }

        [Test]
        public void Length_of_ulong_same_as_uint256([ValueSource(nameof(ULongValues))] ulong value)
        {
            Assert.That(Rlp.LengthOf(value), Is.EqualTo(Rlp.LengthOf((UInt256)value)));
        }

        [Test]
        public void Single_byte_encoding_decoding()
        {
            byte item = 0;
            for (int i = 0; i < 128; i++)
            {
                Assert.That(Rlp.LengthOf(item), Is.EqualTo(1));
                var data = Rlp.Encode(item);
                Rlp.ValueDecoderContext rlp = new(data.Bytes);
                Assert.That(rlp.DecodeByte(), Is.EqualTo(item));

                item += 1;
            }

            for (int i = 128; i < 256; i++)
            {
                Assert.That(Rlp.LengthOf(item), Is.EqualTo(2));
                var data = Rlp.Encode(item);
                Rlp.ValueDecoderContext rlp = new(data.Bytes);
                Assert.That(rlp.DecodeByte(), Is.EqualTo(item));

                item += 1;
            }
        }

        [Test]
        public void Long_encode_decode([ValueSource(nameof(LongValues))] long value, [Values] bool useBuffer)
        {
            Rlp.ValueDecoderContext context = useBuffer
                ? new(Rlp.Encode(value, stackalloc byte[9]).ToArray())
                : new(Rlp.Encode(value).Bytes);

            long decoded = context.DecodeLong();

            Assert.That(decoded, Is.EqualTo(value));
        }

        [Test]
        public void ULong_encode_decode([ValueSource(nameof(ULongValues))] ulong value, [Values] bool useBuffer)
        {
            Rlp.ValueDecoderContext context = useBuffer
                ? new(Rlp.Encode(value, stackalloc byte[9]).ToArray())
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
            Assert.That(rlp, Is.EqualTo(expectedResult), "byte array");
            Assert.That(rlpSpan, Is.EqualTo(expectedResult), "span");
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
            Assert.That(rlp, Is.EqualTo(expectedResult), "byte array");
            Assert.That(rlpSpan, Is.EqualTo(expectedResult), "span");
        }

        [TestCase(128)]
        [TestCase(255)]
        public void Byte_array_of_length_1_and_first_byte_value_equal_or_more_than_128(byte value)
        {
            byte[] bytes = { value };
            Rlp rlp = Rlp.Encode(bytes);
            Rlp rlpSpan = Rlp.Encode(bytes.AsSpan());
            Rlp expectedResult = new(new[] { (byte)129, value });
            Assert.That(rlp, Is.EqualTo(expectedResult), "byte array");
            Assert.That(rlpSpan, Is.EqualTo(expectedResult), "span");
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

            Assert.That(Rlp.Encode(input), Is.EqualTo(expectedResult), "byte array");
            Assert.That(Rlp.Encode(input.AsSpan()), Is.EqualTo(expectedResult), "span");
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

            Assert.That(Rlp.Encode(input), Is.EqualTo(expectedResult), "byte array");
            Assert.That(Rlp.Encode(input.AsSpan()), Is.EqualTo(expectedResult), "span");
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

            Assert.That(Rlp.Encode(input), Is.EqualTo(expectedResult), "byte array");
            Assert.That(Rlp.Encode(input.AsSpan()), Is.EqualTo(expectedResult), "span");
        }

        [TestCase(new byte[] { 128 }, false)]
        [TestCase(new byte[] { 1 }, true)]
        public void Decode_bool(byte[] rlp, bool expectedBool)
        {
            rlp.AsRlpValueContext().DecodeBool().Should().Be(expectedBool);
        }

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
        public void Decode_bool_exceptional_cases(byte[] rlp)
        {
            Assert.Throws<RlpException>(() => rlp.AsRlpValueContext().DecodeBool());
        }

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

            Assert.That(reEncoded.Bytes, Is.EqualTo(original.Bytes));
            Assert.That(reEncoded, Is.SameAs(original));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RlpContextWithSliceMemory_shouldNotCopyUnderlyingData(bool sliceValue)
        {
            byte[] randomBytes = new byte[100];
            Random.Shared.NextBytes(randomBytes);

            int requiredLength = Rlp.LengthOf(randomBytes) * 3;
            RlpStream stream = new RlpStream(requiredLength);
            stream.Encode(randomBytes);
            stream.Encode(randomBytes);
            stream.Encode(randomBytes);

            Memory<byte> memory = stream.Data.ToArray();
            Rlp.ValueDecoderContext context = new Rlp.ValueDecoderContext(memory, sliceValue);

            for (int i = 0; i < 3; i++)
            {
                Memory<byte> slice = context.DecodeByteArrayMemory();
                slice.Should().NotBeNull();
                MemoryMarshal.TryGetArray(slice, out ArraySegment<byte> segment);

                bool isACopy = (segment.Offset == 0 && segment.Count == slice.Length);
                isACopy.Should().NotBe(sliceValue);
            }
        }

        [TestCase(50)]
        [TestCase(100)]
        public void Over_limit_throws(int limit)
        {
            RlpStream stream = Prepare100BytesStream();
            RlpLimit rlpLimit = new(limit);
            if (limit < 100)
            {
                Assert.Throws<RlpLimitException>(() => { Rlp.ValueDecoderContext ctx = new(stream.Data.ToArray()); ctx.DecodeByteArray(rlpLimit); });
            }
            else
            {
                Assert.DoesNotThrow(() => { Rlp.ValueDecoderContext ctx = new(stream.Data.ToArray()); ctx.DecodeByteArray(rlpLimit); });
            }
        }

        [Test]
        public void Not_enough_bytes_throws()
        {
            RlpStream stream = Prepare100BytesStream();
            byte[] data = stream.Data.ToArray()!;
            data[1] = 101; // tamper with length, it is more than available bytes
            Assert.Throws<RlpLimitException>(() => { Rlp.ValueDecoderContext ctx = new(data); ctx.DecodeByteArray(); });
        }

        private static HashSet<long> LongValues()
        {
            const long minusBit = 1L << 63;
            HashSet<long> seen = [];

            for (var i = 0; i < sizeof(long) * 8; i++)
            {
                var pow2 = 1L << i;

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
            for (var i = 0; i < sizeof(long) * 8; i++)
            {
                var pow2 = 1UL << i;

                yield return pow2;
                yield return pow2 - 1;
                yield return pow2 + 1;
            }

            yield return ulong.MaxValue - 1;
            yield return ulong.MaxValue;
        }

        private static IEnumerable<DecoderCase> IntegerDecoders()
        {
            yield return new(nameof(Rlp.ValueDecoderContext.DecodeByte), static ctx => ctx.DecodeByte(), sizeof(byte));
            yield return new(nameof(Rlp.ValueDecoderContext.DecodeUShort), static ctx => ctx.DecodeUShort(), sizeof(ushort));
            yield return new(nameof(Rlp.ValueDecoderContext.DecodeUInt), static ctx => ctx.DecodeUInt(), sizeof(uint));
            yield return new(nameof(Rlp.ValueDecoderContext.DecodeLong), static ctx => ctx.DecodeLong(), sizeof(long));
            yield return new(nameof(Rlp.ValueDecoderContext.DecodeULong), static ctx => ctx.DecodeULong(), sizeof(ulong));
            yield return new(nameof(Rlp.ValueDecoderContext.DecodeUInt256), static ctx => ctx.DecodeUInt256(), 256 / 8);
            yield return new(nameof(Rlp.ValueDecoderContext.DecodeUBigInt), static ctx => ctx.DecodeUBigInt(), null);
        }

        private static RlpStream Prepare100BytesStream()
        {
            byte[] randomBytes = new byte[100];
            Random.Shared.NextBytes(randomBytes);

            int requiredLength = Rlp.LengthOf(randomBytes);
            RlpStream stream = new(requiredLength);
            stream.Encode(randomBytes);
            stream.Reset();
            return stream;
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

                Rlp.ValueDecoderContext ctx = new(data);
                ctx.PeekNextRlpLength().Should().Be(expected, $"ValueDecoderContext prefix {prefix}");

                ValueRlpStream vrs = new(data);
                vrs.PeekNextRlpLength().Should().Be(expected, $"ValueRlpStream prefix {prefix}");
            }
        }

        [TestCase(184, 56)]   // long string, lengthOfLength=1
        [TestCase(185, 256)]  // long string, lengthOfLength=2
        [TestCase(248, 56)]   // long list, lengthOfLength=1
        [TestCase(249, 256)]  // long list, lengthOfLength=2
        public void PeekNextRlpLength_long_form(int prefix, int contentLength)
        {
            byte[] data = BuildLongFormRlp(prefix, contentLength);

            Rlp.ValueDecoderContext ctx = new(data);
            ctx.PeekNextRlpLength().Should().Be(data.Length, $"ValueDecoderContext prefix {prefix}");

            ValueRlpStream vrs = new(data);
            vrs.PeekNextRlpLength().Should().Be(data.Length, $"ValueRlpStream prefix {prefix}");
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

            Rlp.ValueDecoderContext ctx = new(data);
            var (pLen, cLen) = ctx.PeekPrefixAndContentLength();
            pLen.Should().Be(expectedPrefixLen, $"ValueDecoderContext prefix length for {prefix}");
            cLen.Should().Be(expectedContentLen, $"ValueDecoderContext content length for {prefix}");

            ValueRlpStream vrs = new(data);
            var (pLen2, cLen2) = vrs.PeekPrefixAndContentLength();
            pLen2.Should().Be(expectedPrefixLen, $"ValueRlpStream prefix length for {prefix}");
            cLen2.Should().Be(expectedContentLen, $"ValueRlpStream content length for {prefix}");
        }

        [TestCase(184, 56)]   // long string, lengthOfLength=1
        [TestCase(248, 56)]   // long list, lengthOfLength=1
        public void PeekPrefixAndContentLength_long_form(int prefix, int contentLength)
        {
            int lengthOfLength = prefix < 192 ? prefix - 183 : prefix - 247;
            byte[] data = BuildLongFormRlp(prefix, contentLength);

            Rlp.ValueDecoderContext ctx = new(data);
            var (pLen, cLen) = ctx.PeekPrefixAndContentLength();
            pLen.Should().Be(1 + lengthOfLength, $"ValueDecoderContext prefix length for {prefix}");
            cLen.Should().Be(contentLength, $"ValueDecoderContext content length for {prefix}");

            ValueRlpStream vrs = new(data);
            var (pLen2, cLen2) = vrs.PeekPrefixAndContentLength();
            pLen2.Should().Be(1 + lengthOfLength, $"ValueRlpStream prefix length for {prefix}");
            cLen2.Should().Be(contentLength, $"ValueRlpStream content length for {prefix}");
        }

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

        private static void AssertItemCount(byte[] rlp, int expected)
        {
            Rlp.ValueDecoderContext ctx = new(rlp);
            ctx.ReadSequenceLength();
            ctx.PeekNumberOfItemsRemaining().Should().Be(expected);

            ValueRlpStream vrs = new(rlp);
            vrs.ReadSequenceLength();
            vrs.PeekNumberOfItemsRemaining().Should().Be(expected);
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
                Rlp.ValueDecoderContext ctx = new(data);
                ctx.PeekPrefixAndContentLength();
                Assert.Fail("Expected RlpException from ValueDecoderContext");
            }
            catch (RlpException) { }

            try
            {
                ValueRlpStream vrs = new(data);
                vrs.PeekPrefixAndContentLength();
                Assert.Fail("Expected RlpException from ValueRlpStream");
            }
            catch (RlpException) { }

            using IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(owner.Memory.Span);
            Assert.Throws<RlpException>(() => new RlpItemList(owner, owner.Memory[..data.Length]));
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
    }
}
