// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Intrinsics;
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
        [TestCase(null, "0x", -1)]
        [TestCase("0x", null, 1)]
        [TestCase("0x01", "0x01", 0)]
        [TestCase("0x01", "0x0102", -1)]
        [TestCase("0x0102", "0x01", 1)]
        public void Compares_bytes_properly(string? hexString1, string? hexString2, int expectedResult)
        {
            IComparer<byte[]> comparer = Bytes.Comparer;
            byte[]? x = hexString1 is null ? null : Bytes.FromHexString(hexString1);
            byte[]? y = hexString2 is null ? null : Bytes.FromHexString(hexString2);
            Assert.That(comparer.Compare(x, y), Is.EqualTo(expectedResult));
        }

        [TestCase("0x1", 1)]
        [TestCase("0x01", 1)]
        [TestCase("1", 1)]
        [TestCase("01", 1)]
        [TestCase("0x123", 1)]
        [TestCase("0x0123", 1)]
        [TestCase("123", 1)]
        [TestCase("0123", 1)]
        [TestCase("", 0)]
        public void FromHexString(string hexString, byte expectedResult)
        {
            byte[] bytes = Bytes.FromHexString(hexString);
            if (hexString == "")
                Assert.That(expectedResult, Is.EqualTo(bytes.Length), "Bytes array should be empty but is not");
            else
                Assert.That(expectedResult, Is.EqualTo(bytes[0]), "new");
        }

        [TestCase(null)]
        public void FromHexStringThrows(string? hexString)
        {
            Assert.That(() => Bytes.FromHexString(hexString!), Throws.TypeOf<ArgumentNullException>());
        }

        [TestCase("0x07", "0x7", true, true)]
        [TestCase("0x07", "7", false, true)]
        [TestCase("0x07", "0x07", true, false)]
        [TestCase("0x07", "07", false, false)]
        [TestCase("0x77", "0x77", true, true)]
        [TestCase("0x77", "77", false, true)]
        [TestCase("0x77", "0x77", true, false)]
        [TestCase("0x77", "77", false, false)]
        [TestCase("0x0007", "0x7", true, true)]
        [TestCase("0x0007", "7", false, true)]
        [TestCase("0x0007", "0x0007", true, false)]
        [TestCase("0x0007", "0007", false, false)]
        [TestCase("0x0077", "0x77", true, true)]
        [TestCase("0x0077", "77", false, true)]
        [TestCase("0x0077", "0x0077", true, false)]
        [TestCase("0x0077", "0077", false, false)]
        [TestCase("0x0f", "0xF", true, true)]
        [TestCase("0x0F", "F", false, true)]
        [TestCase("0xFf", "0xFf", true, true)]
        [TestCase("0xff", "Ff", false, true)]
        [TestCase("0xfff", "0xFFF", true, true)]
        [TestCase("0xFFF", "FFF", false, true)]
        [TestCase("0xf7f", "0xf7F", true, true)]
        [TestCase("0xf7F", "f7F", false, true)]
        [TestCase("0xffffffaf9f", "0xfFFffFaF9F", true, true)]
        [TestCase("0xfFFffFaF9F", "fFFffFaF9F", false, true)]
        [TestCase("0xcfffffaff9f", "0xcFfFFFafF9F", true, true)]
        [TestCase("0xcFfFFFafF9F", "cFfFFFafF9F", false, true)]
        public void ToHexString(string input, string expectedResult, bool with0x, bool noLeadingZeros)
        {
            byte[] bytes = Bytes.FromHexString(input);
            if (!noLeadingZeros)
            {
                Assert.That(Bytes.ByteArrayToHexViaLookup32Safe(bytes, with0x), Is.EqualTo(expectedResult.ToLower()));
            }
            Assert.That(bytes.ToHexString(with0x, noLeadingZeros), Is.EqualTo(expectedResult.ToLower()));
            Assert.That(bytes.AsSpan().ToHexString(with0x, noLeadingZeros, withEip55Checksum: false), Is.EqualTo(expectedResult.ToLower()));
            Assert.That(new ReadOnlySpan<byte>(bytes).ToHexString(with0x, noLeadingZeros), Is.EqualTo(expectedResult.ToLower()));

            Assert.That(bytes.ToHexString(with0x, noLeadingZeros, withEip55Checksum: true), Is.EqualTo(expectedResult));
            Assert.That(bytes.AsSpan().ToHexString(with0x, noLeadingZeros, withEip55Checksum: true), Is.EqualTo(bytes.ToHexString(with0x, noLeadingZeros, withEip55Checksum: true)));
        }

        [TestCase("0x", "0x", true)]
        [TestCase(null, null, true)]
        //        [TestCase(null, "0x", false)]
        //        [TestCase("0x", null, false)]
        [TestCase("0x01", "0x01", true)]
        [TestCase("0x01", "0x0102", false)]
        [TestCase("0x0102", "0x01", false)]
        public void Compares_bytes_equality_properly(string? hexString1, string? hexString2, bool expectedResult)
        {
            // interestingly, sequence equals that we have been using for some time returns 0x is null, null == 0x
            IEqualityComparer<byte[]> comparer = Bytes.EqualityComparer;
            byte[]? x = hexString1 is null ? null : Bytes.FromHexString(hexString1);
            byte[]? y = hexString2 is null ? null : Bytes.FromHexString(hexString2);
            Assert.That(comparer.Equals(x, y), Is.EqualTo(expectedResult));
        }

        [Test]
        public void Stream_hex_works()
        {
            byte[] bytes = new byte[] { 15, 16, 255 };
            StreamWriter? sw = null;
            StreamReader? sr = null;

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
                    Assert.That(result, Is.EqualTo("0f10ff"));
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
                byte[] before = (byte[])bytes.Clone();
                Assert.That(bytes.Length, Is.EqualTo(32));

                Bytes.Avx2Reverse256InPlace(bytes);
                for (int i = 0; i < 32; i++)
                {
                    Assert.That(bytes[32 - 1 - i], Is.EqualTo(before[i]));
                }

                TestContext.Out.WriteLine(before.ToHexString());
                TestContext.Out.WriteLine(bytes.ToHexString());
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
            Assert.That(bytes.AsSpan().ReadEthUInt32(), Is.EqualTo(expectedResult));
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
            Assert.That(bytes.AsSpan().ReadEthInt32(), Is.EqualTo(expectedResult));
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
            Assert.That(bytes.AsSpan().ReadEthUInt32(), Is.EqualTo(expectedResult));
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
            Assert.That(bytes.AsSpan().ReadEthUInt64(), Is.EqualTo(expectedResult));
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
            Assert.That(value.GetHighestSetBitIndex(), Is.EqualTo(expectedResult));
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
            Assert.That(value.GetBit(position), Is.EqualTo(expectedResult));
        }

        [TestCase("0x", 0)]
        [TestCase("0x1000", 1)]
        [TestCase("0x100000", 2)]
        [TestCase("0x10000000", 3)]
        [TestCase("0x1000000000", 4)]
        [TestCase("0x100000000000", 5)]
        [TestCase("0x10000000000000", 6)]
        [TestCase("0x1000000000000000", 7)]
        [TestCase("0x100000000000000000", 8)]
        [TestCase("0x0000", 2)]
        [TestCase("0x000100", 1)]
        public void Trailing_zeros_count_works(string hex, int expectedResult)
        {
            Assert.That(Bytes.FromHexString(hex).TrailingZerosCount(), Is.EqualTo(expectedResult));
        }

        [TestCase("0x", 0, "0")]
        [TestCase("0x1000", 2, "4096")]
        [TestCase("0x0000", 2, "0")]
        [TestCase("0x000100", 3, "256")]
        [TestCase("0x000100", 32, "256")]
        public void To_signed_big_int(string hex, int length, string expectedResult)
        {
            Assert.That(Bytes.FromHexString(hex).ToSignedBigInteger(length), Is.EqualTo(BigInteger.Parse(expectedResult)));
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
            _ = Bytes.FromHexString(hex).AsSpan().ToBigEndianBitArray256();
        }

        [TestCase("0x0001020304050607080910111213141516171819202122232425262728293031", "0x3130292827262524232221201918171615141312111009080706050403020100")]
        public void Can_create_bit_array_from_bytes(string hex, string expectedResult)
        {
            byte[] input = Bytes.FromHexString(hex);
            Bytes.ReverseInPlace(input);
            Assert.That(Bytes.FromHexString(expectedResult), Is.EqualTo(input));
        }

        [TestCase]
        public void Can_roundtrip_utf8_hex_conversion()
        {
            for (var i = 0; i < 2048; i++)
            {
                byte[] input = new byte[i];
                byte[] hex = new byte[i * 2];
                TestContext.CurrentContext.Random.NextBytes(input);

                Bytes.OutputBytesToByteHex(input, hex, extraNibble: false);

                byte[] output = Bytes.FromUtf8HexString(hex);

                Assert.That(output, Is.EqualTo(input));
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(8)]
        [TestCase(16)]
        [TestCase(32)]
        [TestCase(64)]
        [TestCase(128)]
        [TestCase(256)]
        [TestCase(512)]
        public void Invalid_utf8_hex_conversion_fails(int length)
        {
            byte[] input = new byte[length];
            byte[] hex = new byte[length * 2];
            TestContext.CurrentContext.Random.NextBytes(input);

            Bytes.OutputBytesToByteHex(input, hex, extraNibble: false);

            for (var i = 0; i < hex.Length; i++)
            {
                byte b = hex[i];

                hex[i] = (byte)' ';
                Assert.Throws<FormatException>(() => Bytes.FromUtf8HexString(hex));

                hex[i] = b;
                byte[] output = Bytes.FromUtf8HexString(hex);
                Assert.That(output, Is.EqualTo(input));
            }
        }

        public static IEnumerable<TestCaseData> BitwiseTests
        {
            get
            {
                byte[] GenerateRandom(int length)
                {
                    var bytes = new byte[length];
                    TestContext.CurrentContext.Random.NextBytes(bytes);
                    return bytes;
                }

                TestCaseData GenerateTest(int length) => new(GenerateRandom(length), GenerateRandom(length));

                yield return GenerateTest(0);
                yield return GenerateTest(1);
                yield return GenerateTest(8 - 1);
                yield return GenerateTest(8);
                yield return GenerateTest(8 + 1);
                yield return GenerateTest(Vector128<byte>.Count - 1);
                yield return GenerateTest(Vector128<byte>.Count);
                yield return GenerateTest(Vector128<byte>.Count + 1);
                yield return GenerateTest(Vector256<byte>.Count - 1);
                yield return GenerateTest(Vector256<byte>.Count);
                yield return GenerateTest(Vector256<byte>.Count + 1);
                yield return GenerateTest(Vector256<byte>.Count + Vector128<byte>.Count - 1);
                yield return GenerateTest(Vector256<byte>.Count + Vector128<byte>.Count);
                yield return GenerateTest(Vector256<byte>.Count + Vector128<byte>.Count + 1);
                yield return GenerateTest(Vector512<byte>.Count - 1);
                yield return GenerateTest(Vector512<byte>.Count);
                yield return GenerateTest(Vector512<byte>.Count + 1);
                yield return GenerateTest(Vector512<byte>.Count + Vector256<byte>.Count - 1);
                yield return GenerateTest(Vector512<byte>.Count + Vector256<byte>.Count);
                yield return GenerateTest(Vector512<byte>.Count + Vector256<byte>.Count + 1);
                yield return GenerateTest(Vector512<byte>.Count + Vector256<byte>.Count + Vector128<byte>.Count - 1);
                yield return GenerateTest(Vector512<byte>.Count + Vector256<byte>.Count + Vector128<byte>.Count);
                yield return GenerateTest(Vector512<byte>.Count + Vector256<byte>.Count + Vector128<byte>.Count + 1);
                yield return GenerateTest(Vector512<byte>.Count * 2 - 1);
                yield return GenerateTest(Vector512<byte>.Count * 2);
                yield return GenerateTest(Vector512<byte>.Count * 2 + 1);
                yield return GenerateTest(Vector512<byte>.Count * 3 - 1);
                yield return GenerateTest(Vector512<byte>.Count * 3);
                yield return GenerateTest(Vector512<byte>.Count * 3 + 1);
            }
        }

        private static TestCaseData GenerateTestResult(TestCaseData test, Func<byte, byte, byte> calc)
        {
            byte[]? thisArray = test.Arguments[0] as byte[];
            byte[]? valueArray = test.Arguments[1] as byte[];
            byte[]? resultArray = [.. thisArray!.Zip(valueArray!, calc)];
            return new TestCaseData(thisArray, valueArray, resultArray);
        }

        public static IEnumerable OrTests
        {
            get
            {
                foreach (TestCaseData test in BitwiseTests)
                {
                    yield return GenerateTestResult(test, static (b1, b2) => (byte)(b1 | b2));
                }
            }
        }

        public static IEnumerable XorTests
        {
            get
            {
                foreach (TestCaseData test in BitwiseTests)
                {
                    yield return GenerateTestResult(test, static (b1, b2) => (byte)(b1 ^ b2));
                }
            }
        }

        [TestCaseSource(nameof(OrTests))]
        public void Or(byte[] first, byte[] second, byte[] expected)
        {
            first.AsSpan().Or(second);
            first.Should().Equal(expected);
        }

        [TestCaseSource(nameof(XorTests))]
        public void Xor(byte[] first, byte[] second, byte[] expected)
        {
            first.AsSpan().Xor(second);
            first.Should().Equal(expected);
        }

        [Test]
        public void NullableComparison()
        {
            Bytes.NullableEqualityComparer.Equals(null, null).Should().BeTrue();
        }

        [Test]
        public void FastHash_EmptyInput_ReturnsZero()
        {
            ReadOnlySpan<byte> empty = ReadOnlySpan<byte>.Empty;
            empty.FastHash().Should().Be(0);
        }

        [Test]
        public void FastHash_SameInput_ReturnsSameHash()
        {
            byte[] input = new byte[100];
            TestContext.CurrentContext.Random.NextBytes(input);

            int hash1 = ((ReadOnlySpan<byte>)input).FastHash();
            int hash2 = ((ReadOnlySpan<byte>)input).FastHash();

            hash1.Should().Be(hash2);
        }

        [Test]
        public void FastHash_DifferentInput_ReturnsDifferentHash()
        {
            byte[] input1 = new byte[100];
            byte[] input2 = new byte[100];
            TestContext.CurrentContext.Random.NextBytes(input1);
            Array.Copy(input1, input2, input1.Length);
            input2[50] ^= 0xFF; // Flip bits at position 50

            int hash1 = ((ReadOnlySpan<byte>)input1).FastHash();
            int hash2 = ((ReadOnlySpan<byte>)input2).FastHash();

            hash1.Should().NotBe(hash2);
        }

        // Test cases for the fold-back bug fix: remaining in [49-63] after 64-byte initial load
        // For len=113 to 127, remaining = len-64 = 49 to 63, which requires the last64 fold-back
        [TestCase(113)] // remaining=49, boundary case for last64
        [TestCase(120)] // remaining=56, middle of the gap range
        [TestCase(127)] // remaining=63, upper boundary
        [TestCase(65)]  // remaining=1, lower boundary for >64 path
        [TestCase(80)]  // remaining=16
        [TestCase(96)]  // remaining=32
        [TestCase(112)] // remaining=48, boundary where last64 is NOT needed
        public void FastHash_AllBytesAreHashed_FoldBackCoverage(int length)
        {
            byte[] input = new byte[length];
            TestContext.CurrentContext.Random.NextBytes(input);

            int originalHash = ((ReadOnlySpan<byte>)input).FastHash();

            // Verify that changing any byte changes the hash
            // This catches the gap bug where bytes[64-71] weren't being hashed
            for (int i = 0; i < length; i++)
            {
                byte[] modified = (byte[])input.Clone();
                modified[i] ^= 0xFF;

                int modifiedHash = ((ReadOnlySpan<byte>)modified).FastHash();
                modifiedHash.Should().NotBe(originalHash, $"Changing byte at index {i} should change the hash for length {length}");
            }
        }

        // Specifically test the gap range that was buggy: bytes[64-71] for len=120
        [Test]
        public void FastHash_GapBytesAreHashed_Len120()
        {
            byte[] input = new byte[120];
            TestContext.CurrentContext.Random.NextBytes(input);

            int originalHash = ((ReadOnlySpan<byte>)input).FastHash();

            // The bug was that bytes[64-71] weren't hashed for len=120
            // Test each byte in the gap
            for (int i = 64; i < 72; i++)
            {
                byte[] modified = (byte[])input.Clone();
                modified[i] ^= 0xFF;

                int modifiedHash = ((ReadOnlySpan<byte>)modified).FastHash();
                modifiedHash.Should().NotBe(originalHash, $"Changing byte at index {i} (in gap range) should change the hash");
            }
        }

        // Test medium-large case (33-64 bytes) with overlap to verify it works
        [TestCase(50)] // Tests overlap in medium-large path
        public void FastHash_MediumLarge_AllBytesContribute(int length)
        {
            byte[] input = new byte[length];
            TestContext.CurrentContext.Random.NextBytes(input);

            int originalHash = ((ReadOnlySpan<byte>)input).FastHash();

            // Test ALL bytes to verify overlap handling works
            for (int i = 0; i < length; i++)
            {
                byte[] modified = (byte[])input.Clone();
                modified[i] ^= 0xFF;

                int modifiedHash = ((ReadOnlySpan<byte>)modified).FastHash();
                modifiedHash.Should().NotBe(originalHash, $"Changing byte at index {i} should change the hash for length {length}");
            }
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(15)]
        [TestCase(16)]
        [TestCase(31)]
        [TestCase(32)]
        [TestCase(33)]
        [TestCase(64)]
        [TestCase(128)]
        [TestCase(256)]
        [TestCase(500)]
        public void FastHash_VariousLengths_AllBytesContribute(int length)
        {
            byte[] input = new byte[length];
            TestContext.CurrentContext.Random.NextBytes(input);

            int originalHash = ((ReadOnlySpan<byte>)input).FastHash();

            // Test first, middle, and last bytes to ensure all contribute
            int[] indicesToTest = [0, length / 2, length - 1];
            foreach (int i in indicesToTest)
            {
                byte[] modified = (byte[])input.Clone();
                modified[i] ^= 0xFF;

                int modifiedHash = ((ReadOnlySpan<byte>)modified).FastHash();
                modifiedHash.Should().NotBe(originalHash, $"Changing byte at index {i} should change the hash for length {length}");
            }
        }

        [TestCase(16, 1000u, 2000u)]
        [TestCase(16, 0u, 1u)]
        [TestCase(16, 0u, 0xFFFFFFFFu)]
        [TestCase(20, 1000u, 2000u)]
        [TestCase(32, 1000u, 2000u)]
        [TestCase(33, 1000u, 2000u)]
        [TestCase(64, 1000u, 2000u)]
        [TestCase(65, 1000u, 2000u)]
        [TestCase(71, 1000u, 2000u)]
        [TestCase(79, 1000u, 2000u)]
        [TestCase(80, 1000u, 2000u)]
        [TestCase(128, 1000u, 2000u)]
        public void FastHash_SeedAffectsOutput(int length, uint seed1, uint seed2)
        {
            byte[] input = new byte[length];
            for (int i = 0; i < length; i++)
                input[i] = (byte)(i * 0x17 + 0x42);

            SpanExtensions.FastHash(input, seed1).Should()
                .NotBe(SpanExtensions.FastHash(input, seed2),
                    $"seeds {seed1} vs {seed2} for {length} bytes");
        }
    }
}
