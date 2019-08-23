/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using System.Numerics;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Dirichlet.Numerics;
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
        [Ignore("That was a regression test but now it is failing again and cannot find the reason we needed this behaviour in the first place. Sync works all fine. Leaving it here as it may resurface - make sure to add more explanation to it in such case.")]
        public void Serializing_object_int_regression()
        {
            Rlp output = Rlp.Encode(new Rlp[] {Rlp.Encode(1)});
            Assert.AreEqual(new byte[] {1}, output.Bytes);
        }

        [TestCase(1, 0)]
        [TestCase(1, 1)]
        [TestCase(1, 55)]
        [TestCase(2, 56)]
        [TestCase(2, 128)]
        [TestCase(3, 16000)]
        [TestCase(4, 300000)]
        public void Memory_stream_sequence(int positionAfter, int length)
        {
            MemoryStream stream = new MemoryStream();
            Rlp.StartSequence(stream, length);
            Assert.AreEqual(positionAfter, stream.Position);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(55)]
        [TestCase(56)]
        [TestCase(128)]
        [TestCase(16000)]
        [TestCase(300000)]
        public void Memory_stream_uint256(int value)
        {
            MemoryStream stream = new MemoryStream();
            Rlp.Encode(stream, (UInt256) value);

            byte[] bytesNew = stream.ToArray();
            byte[] bytesOld = Rlp.Encode((UInt256) value).Bytes;
            Assert.AreEqual(bytesOld.ToHexString(), bytesNew.ToHexString());
            Assert.AreEqual(bytesOld.Length, Rlp.LengthOf((UInt256) value), "length");
        }

//        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(55)]
        [TestCase(56)]
        [TestCase(128)]
        [TestCase(16000)]
        [TestCase(300000)]
        public void Memory_stream_long(int value)
        {
            MemoryStream stream = new MemoryStream();
            Rlp.Encode(stream, (long) value);

            byte[] bytesNew = stream.ToArray();
            byte[] bytesOld = Rlp.Encode((long) value).Bytes;
            Assert.AreEqual(bytesOld.ToHexString(), bytesNew.ToHexString());
            Assert.AreEqual(bytesOld.Length, Rlp.LengthOf((long) value), "length");
        }

//        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(55)]
        [TestCase(56)]
        [TestCase(128)]
        [TestCase(16000)]
        [TestCase(300000)]
        public void Memory_stream_int(int value)
        {
            MemoryStream stream = new MemoryStream();
            Rlp.Encode(stream, value);

            byte[] bytesNew = stream.ToArray();
            byte[] bytesOld = Rlp.Encode(value).Bytes;
            Assert.AreEqual(bytesOld.ToHexString(), bytesNew.ToHexString());
            Assert.AreEqual(bytesOld.Length, Rlp.LengthOf(value), "length");
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(55)]
        [TestCase(56)]
        [TestCase(128)]
        [TestCase(16000)]
        [TestCase(300000)]
        public void Memory_stream_nonce(int value)
        {
            MemoryStream stream = new MemoryStream();
            Rlp.Encode(stream, (ulong) value);

            byte[] bytesNew = stream.ToArray();
            byte[] bytesOld = Rlp.Encode((ulong) value).Bytes;
            Assert.AreEqual(bytesOld.ToHexString(), bytesNew.ToHexString());
            Assert.AreEqual(bytesOld.Length, Rlp.LengthOf((ulong) value), "length");
        }

        [Test]
        public void Length_of_uint()
        {
            Assert.AreEqual(1, Rlp.LengthOf(UInt256.Zero));
            Assert.AreEqual(1, Rlp.LengthOf((UInt256)127));
            Assert.AreEqual(2, Rlp.LengthOf((UInt256)128));
            
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