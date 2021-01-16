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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BytesTests
    {
        [TestCase("0x", "0x", 0)]
        [TestCase(null, null, 0)]
        [TestCase(null, "0x", 1)]
        [TestCase("0x", null, -1)]
        [TestCase("0x01", "0x01", 0)]
        [TestCase("0x01", "0x0102", 1)]
        [TestCase("0x0102", "0x01", -1)]
        public void Compares_bytes_properly(string hexString1, string hexString2, int expectedResult)
        {
            IComparer<byte[]> comparer = Bytes.Comparer;
            byte[] x = hexString1 == null ? null : Bytes.FromHexString(hexString1);
            byte[] y = hexString2 == null ? null : Bytes.FromHexString(hexString2);
            Assert.AreEqual(expectedResult, comparer.Compare(x, y));
        }

        [TestCase("0x1", 1)]
        [TestCase("0x01", 1)]
        [TestCase("1", 1)]
        [TestCase("01", 1)]
        [TestCase("0x123", 1)]
        [TestCase("0x0123", 1)]
        [TestCase("123", 1)]
        [TestCase("0123", 1)]
        public void FromHexString(string hexString, byte expectedResult)
        {
            byte[] bytesOld = Bytes.FromHexStringOld(hexString);
            Assert.AreEqual(bytesOld[0], expectedResult, "old");

            byte[] bytes = Bytes.FromHexString(hexString);
            Assert.AreEqual(bytes[0], expectedResult, "new");
        }

        [TestCase("0x07", "0x7", true, true)]
        [TestCase("0x07", "7", false, true)]
        [TestCase("0x07", "0x07", true, false)]
        [TestCase("0x07", "07", false, false)]
        [TestCase("0x0007", "0x7", true, true)]
        [TestCase("0x0007", "7", false, true)]
        [TestCase("0x0007", "0x0007", true, false)]
        [TestCase("0x0007", "0007", false, false)]
        public void ToHexString(string input, string expectedResult, bool with0x, bool noLeadingZeros)
        {
            byte[] bytes = Bytes.FromHexString(input);
            Assert.AreEqual(expectedResult, bytes.ToHexString(with0x, noLeadingZeros));
        }

        [TestCase("0x", "0x", true)]
        [TestCase(null, null, true)]
//        [TestCase(null, "0x", false)]
//        [TestCase("0x", null, false)]
        [TestCase("0x01", "0x01", true)]
        [TestCase("0x01", "0x0102", false)]
        [TestCase("0x0102", "0x01", false)]
        public void Compares_bytes_equality_properly(string hexString1, string hexString2, bool expectedResult)
        {
            // interestingly, sequence equals that we have been using for some time returns 0x == null, null == 0x
            IEqualityComparer<byte[]> comparer = Bytes.EqualityComparer;
            byte[] x = hexString1 == null ? null : Bytes.FromHexString(hexString1);
            byte[] y = hexString2 == null ? null : Bytes.FromHexString(hexString2);
            Assert.AreEqual(expectedResult, comparer.Equals(x, y));
        }

        [Test]
        public void Stream_hex_works()
        {
            byte[] bytes = new byte[] {15, 16, 255};
            StreamWriter sw = null;
            StreamReader sr = null;

            try
            {
                using (var ms = new MemoryStream())
                {
                    sw = new StreamWriter(ms);
                    sr = new StreamReader(ms);

                    bytes.StreamHex(sw);
                    sw.Flush();

                    ms.Position = 0;

                    string result = sr.ReadToEnd();
                    Assert.AreEqual("0f10ff", result);
                }
            }
            finally
            {
                sw?.Dispose();
                sr?.Dispose();
            }
        }

        [Test]
        public void Reversal()
        {
            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
            {
                byte[] bytes = Bytes.FromHexString("0x000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
                byte[] before = bytes.Clone() as byte[];
                Assert.AreEqual(32, bytes.Length);

                Bytes.Avx2Reverse256InPlace(bytes);
                for (int i = 0; i < 32; i++)
                {
                    Assert.AreEqual(before[i], bytes[32 - 1 - i]);
                }

                TestContext.WriteLine(before.ToHexString());
                TestContext.WriteLine(bytes.ToHexString());
            }
        }

        [TestCase("0x00000000", 0U)]
        [TestCase("0x00000001", 1U)]
        [TestCase("0x00000100", 256U)]
        [TestCase("0x00010000", 256U * 256U)]
        [TestCase("0x01000000", 256U * 256U * 256U)]
        [TestCase("0x01", 1U)]
        [TestCase("0x0100", 256U)]
        [TestCase("0x010000", 256U * 256U)]
        [TestCase("0xffffffff", 4294967295U)]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000001000", 4096U)]
        public void ToUInt32(string hexString, uint expectedResult)
        {
            byte[] bytes = Bytes.FromHexString(hexString);
            Assert.AreEqual(expectedResult, bytes.AsSpan().ReadEthUInt32());
        }

        [TestCase("0x00000000", 0)]
        [TestCase("0x00000001", 1)]
        [TestCase("0x00000100", 256)]
        [TestCase("0x00010000", 256 * 256)]
        [TestCase("0x01000000", 256 * 256 * 256)]
        [TestCase("0x01", 1)]
        [TestCase("0x0100", 256)]
        [TestCase("0x010000", 256 * 256)]
        [TestCase("0xffffffff", -1)]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000001000", 4096)]
        public void ToInt32(string hexString, int expectedResult)
        {
            byte[] bytes = Bytes.FromHexString(hexString);
            Assert.AreEqual(expectedResult, bytes.AsSpan().ReadEthInt32());
        }

        [TestCase("0x00000000", 0U)]
        [TestCase("0x00000001", 1U)]
        [TestCase("0x00000100", 256U)]
        [TestCase("0x00010000", 256U * 256U)]
        [TestCase("0x01000000", 256U * 256U * 256U)]
        [TestCase("0x01", 1U)]
        [TestCase("0x0100", 256U)]
        [TestCase("0x010000", 256U * 256U)]
        [TestCase("0xffffffff", 4294967295U)]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000010000000", 268435456U)]
        public void ToUInt64(string hexString, uint expectedResult)
        {
            byte[] bytes = Bytes.FromHexString(hexString);
            Assert.AreEqual(expectedResult, bytes.AsSpan().ReadEthUInt32());
        }

        [TestCase("0x0000000000000000", 0UL)]
        [TestCase("0x0000000000000001", 1UL)]
        [TestCase("0x0000000000000100", 256UL)]
        [TestCase("0x0000000000010000", 256UL * 256UL)]
        [TestCase("0x0000000001000000", 256UL * 256UL * 256UL)]
        [TestCase("0x0000000100000000", 256UL * 256UL * 256UL * 256UL)]
        [TestCase("0x0000010000000000", 256UL * 256UL * 256UL * 256UL * 256UL)]
        [TestCase("0x0001000000000000", 256UL * 256UL * 256UL * 256UL * 256UL * 256UL)]
        [TestCase("0x0100000000000000", 256UL * 256UL * 256UL * 256UL * 256UL * 256UL * 256UL)]
        [TestCase("0x01", 1UL)]
        [TestCase("0x0100", 256UL)]
        [TestCase("0x010000", 256UL * 256UL)]
        [TestCase("0x01000000", 256UL * 256UL * 256UL)]
        [TestCase("0x0100000000", 256UL * 256UL * 256UL * 256UL)]
        [TestCase("0x010000000000", 256UL * 256UL * 256UL * 256UL * 256UL)]
        [TestCase("0x01000000000000", 256UL * 256UL * 256UL * 256UL * 256UL * 256UL)]
        [TestCase("0xffffffffffffffff", 18446744073709551615UL)]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000010000000", 268435456UL)]
        public void ToInt64(string hexString, ulong expectedResult)
        {
            byte[] bytes = Bytes.FromHexString(hexString);
            Assert.AreEqual(expectedResult, bytes.AsSpan().ReadEthUInt64());
        }

        [TestCase(0, 0)]
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(4, 3)]
        [TestCase(8, 4)]
        [TestCase(16, 5)]
        [TestCase(32, 6)]
        [TestCase(64, 7)]
        [TestCase(128, 8)]
        [TestCase(255, 8)]
        [TestCase(79, 7)]
        public void Can_get_highest_bit_set(byte value, int expectedResult)
        {
            Assert.AreEqual(expectedResult, value.GetHighestSetBitIndex());
        }

        [TestCase(255, 0, true)]
        [TestCase(255, 1, true)]
        [TestCase(255, 2, true)]
        [TestCase(255, 3, true)]
        [TestCase(255, 4, true)]
        [TestCase(255, 5, true)]
        [TestCase(255, 6, true)]
        [TestCase(255, 7, true)]
        [TestCase(0, 0, false)]
        [TestCase(0, 1, false)]
        [TestCase(0, 2, false)]
        [TestCase(0, 3, false)]
        [TestCase(0, 4, false)]
        [TestCase(0, 5, false)]
        [TestCase(0, 6, false)]
        [TestCase(0, 7, false)]
        public void Get_bit_works(byte value, int position, bool expectedResult)
        {
            Assert.AreEqual(expectedResult, value.GetBit(position));
        }

        [TestCase("0x", 0)]
        [TestCase("0x1000", 1)]
        [TestCase("0x0000", 2)]
        [TestCase("0x000100", 1)]
        public void Trailing_zeros_count_works(string hex, int expectedResult)
        {
            Assert.AreEqual(expectedResult, Bytes.FromHexString(hex).TrailingZerosCount());
        }

        [TestCase("0x", 0, "0")]
        [TestCase("0x1000", 2, "4096")]
        [TestCase("0x0000", 2, "0")]
        [TestCase("0x000100", 3, "256")]
        [TestCase("0x000100", 32, "256")]
        public void To_signed_big_int(string hex, int length, string expectedResult)
        {
            Assert.AreEqual(BigInteger.Parse(expectedResult), Bytes.FromHexString(hex).ToSignedBigInteger(length));
        }
        
        [TestCase("0x0123456789abcdef0123456789abcdef", "0xefcdab8967452301efcdab8967452301")]
        [TestCase(
            "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "0xefcdab8967452301efcdab8967452301efcdab8967452301efcdab8967452301")]
        public void Can_change_endianness(string hex, string expectedResult)
        {
            byte[] bytes = Bytes.FromHexString(hex);
            Bytes.ChangeEndianness8(bytes);
            bytes.ToHexString(true).Should().Be(expectedResult);
        }
        
        [TestCase("0x0001020304050607080910111213141516171819202122232425262728293031")]
        public void Can_create_bit_array_from_bytes(string hex)
        {
            BitArray result = Bytes.FromHexString(hex).AsSpan().ToBigEndianBitArray256();
        }
        
        [TestCase("0x0001020304050607080910111213141516171819202122232425262728293031", "0x3130292827262524232221201918171615141312111009080706050403020100")]
        public void Can_create_bit_array_from_bytes(string hex, string expectedResult)
        {
            byte[] input = Bytes.FromHexString(hex);
            Bytes.ReverseInPlace(input);
            Assert.AreEqual(input, Bytes.FromHexString(expectedResult));
        }

        public static IEnumerable OrTests
        {
            get
            {
                byte[] GenerateRandom(int length)
                {
                    var bytes = new byte[length];
                    TestContext.CurrentContext.Random.NextBytes(bytes);
                    return bytes;
                }

                TestCaseData GenerateTest(int length)
                {
                    var thisArray = GenerateRandom(length);
                    var valueArray = GenerateRandom(length);
                    var resultArray = thisArray.Zip(valueArray, (b1, b2) => b1 | b2).Select(b => (byte) b).ToArray();
                    return new TestCaseData(thisArray, valueArray, resultArray);
                }
                
                yield return GenerateTest(1);
                yield return GenerateTest(10);
                yield return GenerateTest(32);
                yield return GenerateTest(33);
                yield return GenerateTest(48);
                yield return GenerateTest(128);
                yield return GenerateTest(200);
            }
        }
        
        [TestCaseSource(nameof(OrTests))]
        public void Or(byte[] first, byte[] second, byte[] expected)
        {
            first.AsSpan().Or(second);
            first.Should().Equal(expected);
        }
    }
}
