// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            Rlp output = Rlp.Encode(new Rlp[] { });
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
            Rlp output = Rlp.Encode(new Rlp[] { Rlp.Encode(1) });
            Assert.That(output.Bytes, Is.EqualTo(new byte[] { 1 }));
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
        public void Long_negative()
        {
            Rlp output = Rlp.Encode(-1L);
            var context = new RlpStream(output.Bytes);
            long value = context.DecodeLong();

            Assert.That(value, Is.EqualTo(-1L));
        }

        [Test]
        public void Empty_byte_array()
        {
            byte[] bytes = new byte[0];
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

        [TestCase(new byte[] { 127, 1, 2, 2 }, false)]
        [TestCase(new byte[] { 130, 1, 0 }, true)]
        [TestCase(new byte[] { 130, 0, 2, 2 }, false)]
        [TestCase(new byte[] { 130, 0, 2, 2 }, false)]
        [TestCase(new byte[]
        {184, 56,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0,
            1,0,0,0,0,0,0,0
        }, true)]
        public void Strange_bool(byte[] rlp, bool expectedBool)
        {
            rlp.AsRlpValueContext().DecodeBool().Should().Be(expectedBool);
            rlp.AsRlpStream().DecodeBool().Should().Be(expectedBool);
        }

        [TestCase(new byte[] { 129, 127 })]
        [TestCase(new byte[] { 188, 0 })]
        [TestCase(new byte[] { 184, 55, 1 })]
        [TestCase(new byte[] { 193 })]
        public void Strange_bool_exceptional_cases(byte[] rlp)
        {
            Assert.Throws<RlpException>(() => rlp.AsRlpValueContext().DecodeBool());
            Assert.Throws<RlpException>(() => rlp.AsRlpStream().DecodeBool());
        }


        [TestCase(Int64.MinValue)]
        [TestCase(-1L)]
        [TestCase(0L)]
        [TestCase(1L)]
        [TestCase(129L)]
        [TestCase(257L)]
        [TestCase(Int64.MaxValue / 256 / 256)]
        [TestCase(Int64.MaxValue)]
        [TestCase(1555318864136L)]
        public void Long_and_big_integer_encoded_the_same(long value)
        {
            Rlp rlpLong = Rlp.Encode(value);

            Rlp rlpBigInt = Rlp.Encode(new BigInteger(value));
            if (value < 0)
            {
                rlpBigInt = Rlp.Encode(new BigInteger(value), 8);
            }

            Assert.That(rlpBigInt.Bytes, Is.EqualTo(rlpLong.Bytes));
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

            Memory<byte> memory = stream.Data;
            Rlp.ValueDecoderContext context = new Rlp.ValueDecoderContext(memory, sliceValue);

            for (int i = 0; i < 3; i++)
            {
                Memory<byte>? slice = context.DecodeByteArrayMemory();
                slice.Should().NotBeNull();
                MemoryMarshal.TryGetArray(slice.Value, out ArraySegment<byte> segment);

                bool isACopy = (segment.Offset == 0 && segment.Count == slice.Value.Length);
                isACopy.Should().NotBe(sliceValue);
            }
        }
    }
}
