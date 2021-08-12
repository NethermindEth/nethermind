//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Numerics;
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
                Rlp.Encode(new byte[] {255}));
            Assert.AreEqual(new byte[] {196, 129, 255, 129, 255}, output.Bytes);
        }

        [Test]
        public void Serializing_empty_sequence()
        {
            Rlp output = Rlp.Encode(new Rlp[] { });
            Assert.AreEqual(new byte[] {192}, output.Bytes);
        }

        [Test]
        public void Serializing_sequence_with_one_int_regression()
        {
            Rlp output = Rlp.Encode(new[] {Rlp.Encode(1)});
            Assert.AreEqual(new byte[] {193, 1}, output.Bytes);
        }

        [Test]
        [Explicit("That was a regression test but now it is failing again and cannot find the reason we needed this behaviour in the first place. Sync works all fine. Leaving it here as it may resurface - make sure to add more explanation to it in such case.")]
        public void Serializing_object_int_regression()
        {
            Rlp output = Rlp.Encode(new Rlp[] {Rlp.Encode(1)});
            Assert.AreEqual(new byte[] {1}, output.Bytes);
        }

        [Test]
        public void Length_of_uint()
        {
            Assert.AreEqual(1, Rlp.LengthOf(UInt256.Zero));
            Assert.AreEqual(1, Rlp.LengthOf((UInt256) 127));
            Assert.AreEqual(2, Rlp.LengthOf((UInt256) 128));

            UInt256 item = 255;
            for (int i = 0; i < 32; i++)
            {
                Assert.AreEqual(i + 2, Rlp.LengthOf(item));
                item *= 256;
            }
        }

        [Test]
        public void Long_negative()
        {
            Rlp output = Rlp.Encode(-1L);
            var context = new RlpStream(output.Bytes);
            long value = context.DecodeLong();

            Assert.AreEqual(-1L, value);
        }

        [Test]
        public void Empty_byte_array()
        {
            byte[] bytes = new byte[0];
            Rlp rlp = Rlp.Encode(bytes);
            Rlp rlpSpan = Rlp.Encode(bytes.AsSpan());
            Rlp expectedResult = new Rlp(new byte[] {128});
            Assert.AreEqual(expectedResult, rlp, "byte array");
            Assert.AreEqual(expectedResult, rlpSpan, "span");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(127)]
        public void Byte_array_of_length_1_and_first_byte_value_less_than_128(byte value)
        {
            byte[] bytes = {value};
            Rlp rlp = Rlp.Encode(bytes);
            Rlp rlpSpan = Rlp.Encode(bytes.AsSpan());
            Rlp expectedResult = new Rlp(new[] {value});
            Assert.AreEqual(expectedResult, rlp, "byte array");
            Assert.AreEqual(expectedResult, rlpSpan, "span");
        }

        [TestCase(128)]
        [TestCase(255)]
        public void Byte_array_of_length_1_and_first_byte_value_equal_or_more_than_128(byte value)
        {
            byte[] bytes = {value};
            Rlp rlp = Rlp.Encode(bytes);
            Rlp rlpSpan = Rlp.Encode(bytes.AsSpan());
            Rlp expectedResult = new Rlp(new[] {(byte) 129, value});
            Assert.AreEqual(expectedResult, rlp, "byte array");
            Assert.AreEqual(expectedResult, rlpSpan, "span");
        }

        [Test]
        public void Byte_array_of_length_55()
        {
            byte[] input = new byte[55];
            input[0] = 255;
            input[1] = 128;
            input[2] = 1;

            byte[] expectedResultBytes = new byte[1 + input.Length];
            expectedResultBytes[0] = (byte) (128 + input.Length);
            expectedResultBytes[1] = input[0];
            expectedResultBytes[2] = input[1];
            expectedResultBytes[3] = input[2];

            Rlp expectedResult = new Rlp(expectedResultBytes);

            Assert.AreEqual(expectedResult, Rlp.Encode(input), "byte array");
            Assert.AreEqual(expectedResult, Rlp.Encode(input.AsSpan()), "span");
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
            expectedResultBytes[1] = (byte) input.Length;
            expectedResultBytes[2] = input[0];
            expectedResultBytes[3] = input[1];
            expectedResultBytes[4] = input[2];

            Rlp expectedResult = new Rlp(expectedResultBytes);

            Assert.AreEqual(expectedResult, Rlp.Encode(input), "byte array");
            Assert.AreEqual(expectedResult, Rlp.Encode(input.AsSpan()), "span");
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
            expectedResultBytes[1] = (byte) (input.Length / (16 * 16));
            expectedResultBytes[2] = (byte) (input.Length % (16 * 16));
            expectedResultBytes[3] = input[0];
            expectedResultBytes[4] = input[1];
            expectedResultBytes[5] = input[2];

            Rlp expectedResult = new Rlp(expectedResultBytes);

            Assert.AreEqual(expectedResult, Rlp.Encode(input), "byte array");
            Assert.AreEqual(expectedResult, Rlp.Encode(input.AsSpan()), "span");
        }

        [TestCase(new byte[] {127, 1, 2, 2}, false)]
        [TestCase(new byte[] {130, 1, 0}, true)]
        [TestCase(new byte[] {130, 0, 2, 2}, false)]
        [TestCase(new byte[] {130, 0, 2, 2}, false)]
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
        
        [TestCase(new byte[] {129, 127})]
        [TestCase(new byte[] {188, 0})]
        [TestCase(new byte[] {184, 55, 1})]
        [TestCase(new byte[] {193})]
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

            Assert.AreEqual(rlpLong.Bytes, rlpBigInt.Bytes);
        }
    }
}
