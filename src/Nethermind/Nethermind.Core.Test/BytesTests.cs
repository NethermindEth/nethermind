// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Arm = System.Runtime.Intrinsics.Arm;
using x64 = System.Runtime.Intrinsics.X86;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BytesTests
    {
        private static string CreateHexString(int byteLength)
        {
            char[] chars = new char[byteLength * 2];
            for (int i = 0; i < byteLength; i++)
            {
                HexConverter.ToCharsBuffer((byte)i, chars, i * 2);
            }

            return new string(chars);
        }

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

        [TestCase("1234", 2, new byte[] { 0x12, 0x34 })]
        [TestCase("0x1234", 2, new byte[] { 0x12, 0x34 })]
        [TestCase("1234", 4, new byte[] { 0x00, 0x00, 0x12, 0x34 })]
        [TestCase("123", 2, new byte[] { 0x01, 0x23 })]
        public void FromHexString_with_length_returns_requested_length(string hexString, int length, byte[] expected)
        {
            byte[] bytes = Bytes.FromHexString(hexString, length);

            Assert.That(bytes, Is.EqualTo(expected));
        }

        [Test]
        public void FromHexString_with_length_zero_pads_large_prefix()
        {
            byte[] bytes = Bytes.FromHexString("1234", 512);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(bytes, Has.Length.EqualTo(512));
                Assert.That(bytes[..510], Is.All.Zero);
                Assert.That(bytes[^2..], Is.EqualTo(new byte[] { 0x12, 0x34 }));
            }
        }

        [TestCase("", true)]
        [TestCase("0123456789abcdefABCDEF", true)]
        [TestCase("0123456789abcdefABCDEF0123456789abcdef", true)]
        [TestCase("0123456789abcdefg", false)]
        [TestCase("0123456789abcdef\u0100", false)]
        public void TryCopyHexToUtf8_validates_and_copies_hex_chars(string hex, bool expected)
        {
            byte[] utf8 = new byte[hex.Length];

            bool actual = HexConverter.TryCopyHexToUtf8(hex, utf8);

            Assert.That(actual, Is.EqualTo(expected));
            if (expected)
            {
                for (int i = 0; i < hex.Length; i++)
                {
                    Assert.That(utf8[i], Is.EqualTo((byte)hex[i]));
                }
            }
        }

        [TestCase(32)]
        [TestCase(64)]
        [TestCase(128)]
        public void FromHexString_large_even_length_matches_expected_bytes(int byteLength)
        {
            string hex = CreateHexString(byteLength);

            byte[] bytes = Bytes.FromHexString(hex);

            Assert.That(bytes.Length, Is.EqualTo(byteLength));
            for (int i = 0; i < bytes.Length; i++)
            {
                Assert.That(bytes[i], Is.EqualTo((byte)i));
            }
        }

        [TestCase(null)]
        public void FromHexStringThrows(string? hexString) => Assert.That(() => Bytes.FromHexString(hexString!), Throws.TypeOf<ArgumentNullException>());

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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bytes.ToHexString(with0x, noLeadingZeros), Is.EqualTo(expectedResult.ToLower()));
                Assert.That(bytes.AsSpan().ToHexString(with0x, noLeadingZeros, withEip55Checksum: false), Is.EqualTo(expectedResult.ToLower()));
                Assert.That(new ReadOnlySpan<byte>(bytes).ToHexString(with0x, noLeadingZeros), Is.EqualTo(expectedResult.ToLower()));

                Assert.That(bytes.ToHexString(with0x, noLeadingZeros, withEip55Checksum: true), Is.EqualTo(expectedResult));
                Assert.That(bytes.AsSpan().ToHexString(with0x, noLeadingZeros, withEip55Checksum: true), Is.EqualTo(bytes.ToHexString(with0x, noLeadingZeros, withEip55Checksum: true)));
            }
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
                using (MemoryStream ms = new())
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
        public void Can_get_highest_bit_set(byte value, int expectedResult) => Assert.That(value.GetHighestSetBitIndex(), Is.EqualTo(expectedResult));

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
        public void Get_bit_works(byte value, int position, bool expectedResult) => Assert.That(value.GetBit(position), Is.EqualTo(expectedResult));

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
        public void Trailing_zeros_count_works(string hex, int expectedResult) => Assert.That(Bytes.FromHexString(hex).TrailingZerosCount(), Is.EqualTo(expectedResult));

        [TestCase("0x", 0, "0")]
        [TestCase("0x1000", 2, "4096")]
        [TestCase("0x0000", 2, "0")]
        [TestCase("0x000100", 3, "256")]
        [TestCase("0x000100", 32, "256")]
        public void To_signed_big_int(string hex, int length, string expectedResult) => Assert.That(Bytes.FromHexString(hex).ToSignedBigInteger(length), Is.EqualTo(BigInteger.Parse(expectedResult)));

        [TestCase("0x0123456789abcdef0123456789abcdef", "0xefcdab8967452301efcdab8967452301")]
        [TestCase(
            "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "0xefcdab8967452301efcdab8967452301efcdab8967452301efcdab8967452301")]
        public void Can_change_endianness(string hex, string expectedResult)
        {
            byte[] bytes = Bytes.FromHexString(hex);
            Bytes.ChangeEndianness8(bytes);
            Assert.That(bytes.ToHexString(true), Is.EqualTo(expectedResult));
        }

        [TestCase("0x0001020304050607080910111213141516171819202122232425262728293031")]
        public void Can_create_bit_array_from_bytes(string hex) => _ = Bytes.FromHexString(hex).AsSpan().ToBigEndianBitArray256();

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
            for (int i = 0; i < 2048; i++)
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

            for (int i = 0; i < hex.Length; i++)
            {
                byte b = hex[i];

                hex[i] = (byte)' ';
                Assert.Throws<FormatException>(() => Bytes.FromUtf8HexString(hex));

                hex[i] = b;
                byte[] output = Bytes.FromUtf8HexString(hex);
                Assert.That(output, Is.EqualTo(input));
            }
        }

        [TestCase("0x", 0L)]
        [TestCase("0x00", 0L)]
        [TestCase("0x0000", 0L)]
        [TestCase("0x0000000000000000", 0L)]
        [TestCase("0x000000000000000000", 0L)] // >8 but all prefix zeros -> still 0
        [TestCase("0x01", 1L)]
        [TestCase("0x7f", 127L)]
        [TestCase("0x80", 128L)]
        [TestCase("0xff", 255L)]
        // Leading zeros (any length)
        [TestCase("0x0001", 1L)]
        [TestCase("0x0000000000000001", 1L)]
        [TestCase("0x00000000000000000000000000000001", 1L)]
        // 2-7 bytes (never overflow long)
        [TestCase("0x0100", 256L)]
        [TestCase("0x7fff", 32767L)]
        [TestCase("0x8000", 32768L)]
        [TestCase("0xffffff", 16777215L)]
        [TestCase("0x01000000", 16777216L)]
        [TestCase("0x7fffffffffff", 140737488355327L)] // 6 bytes
        [TestCase("0xffffffffffffff", 72057594037927935L)] // 7 bytes
        // Exactly 8 bytes - valid only if top bit is 0
        [TestCase("0x0000000000000000", 0L)]
        [TestCase("0x0000000000000001", 1L)]
        [TestCase("0x00000000ffffffff", 4294967295L)]
        [TestCase("0x7fffffffffffffff", long.MaxValue)] // boundary
        // >8 bytes with zero-prefix then 8-byte tail
        [TestCase("0x00000000000000007fffffffffffffff", long.MaxValue)]
        [TestCase("0x000000000000000000000000000000007fffffffffffffff", long.MaxValue)]
        [TestCase("0x0000000000000000000000000000000000000000000000000000000000000001", 1L)]
        public void ToPositiveLong_Success(string hex, long expected)
        {
            byte[] bytes = Bytes.FromHexString(hex);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(bytes.ToPositiveLong(), Is.EqualTo(expected));
                Assert.That(bytes.AsSpan().ToPositiveLong(), Is.EqualTo(expected));
                Assert.That(new ReadOnlySpan<byte>(bytes).ToPositiveLong(), Is.EqualTo(expected));
            }
        }

        [TestCase("0x8000000000000000")] // 8 bytes, top bit set (== 2^63)
        [TestCase("0xffffffffffffffff")] // 8 bytes, top bit set
        [TestCase("0x010000000000000000")] // 9 bytes, prefix non-zero (>= 2^64)
        [TestCase("0x00000000000000008000000000000000")] // >8, prefix zeros but tail top bit set
        [TestCase("0x0000000000000000ffffffffffffffff")] // >8, prefix zeros but tail top bit set
        [TestCase("0x000000000000000000000000000000008000000000000000")] // lots of leading zeros then overflow tail
        public void ToPositiveLong_Overflow(string hex)
        {
            byte[] bytes = Bytes.FromHexString(hex);

            Assert.Throws<OverflowException>(() => bytes.ToPositiveLong());
            Assert.Throws<OverflowException>(() => bytes.AsSpan().ToPositiveLong());
            Assert.Throws<OverflowException>(() => new ReadOnlySpan<byte>(bytes).ToPositiveLong());
        }

        // "Prefix non-zero but tail small" should still overflow (value has >64 bits)
        [TestCase("0x010000000000000001")] // prefix 0x01 then tail 1 - still huge
        [TestCase("0x0001ffffffffffffffff")] // prefix contains non-zero (0x01) before last 8 bytes
        [TestCase("0x00ff0000000000000001")] // prefix has 0xff, tail 1
        public void ToPositiveLong_PrefixNonZero_Overflow(string hex)
        {
            byte[] bytes = Bytes.FromHexString(hex);

            Assert.Throws<OverflowException>(() => bytes.ToPositiveLong());
        }

        // Some sanity checks for equivalence with BigInteger path on random-ish patterns.
        // (Not "exhaustive", but good at catching endian mistakes.)
        [TestCase("0x0123456789abcdef", 0x0123456789abcdefL)]
        [TestCase("0x000123456789abcdef", 0x0123456789abcdefL)]
        [TestCase("0x00000000000000000123456789abcdef", 0x0123456789abcdefL)]
        [TestCase("0x000000000000000000000000000000000000000000000000000123456789abcdef", 0x0123456789abcdefL)]
        public void ToPositiveLong_EndiannessAndLeadingZeros(string hex, long expected)
        {
            byte[] bytes = Bytes.FromHexString(hex);
            Assert.That(bytes.ToPositiveLong(), Is.EqualTo(expected));
        }

        public static IEnumerable<TestCaseData> BitwiseTests
        {
            get
            {
                byte[] GenerateRandom(int length)
                {
                    byte[] bytes = new byte[length];
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
            Assert.That(first, Is.EqualTo(expected));
        }

        [TestCaseSource(nameof(XorTests))]
        public void Xor(byte[] first, byte[] second, byte[] expected)
        {
            first.AsSpan().Xor(second);
            Assert.That(first, Is.EqualTo(expected));
        }

        [Test]
        public void NullableComparison() => Assert.That(Bytes.NullableEqualityComparer.Equals(null, null), Is.True);

        [Test]
        public void FastHash_EmptyInput_ReturnsZero()
        {
            ReadOnlySpan<byte> empty = ReadOnlySpan<byte>.Empty;
            Assert.That(empty.FastHash(), Is.EqualTo(0));
        }

        [Test]
        public void FastHash_SameInput_ReturnsSameHash()
        {
            byte[] input = new byte[100];
            TestContext.CurrentContext.Random.NextBytes(input);

            int hash1 = ((ReadOnlySpan<byte>)input).FastHash();
            int hash2 = ((ReadOnlySpan<byte>)input).FastHash();

            Assert.That(hash1, Is.EqualTo(hash2));
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

            Assert.That(hash1, Is.Not.EqualTo(hash2));
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
                Assert.That(modifiedHash, Is.Not.EqualTo(originalHash), $"Changing byte at index {i} should change the hash for length {length}");
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
                Assert.That(modifiedHash, Is.Not.EqualTo(originalHash), $"Changing byte at index {i} (in gap range) should change the hash");
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
                Assert.That(modifiedHash, Is.Not.EqualTo(originalHash), $"Changing byte at index {i} should change the hash for length {length}");
            }
        }

        private static readonly int[] FastHashLengths = [1, 4, 5, 7, 8, 15, 16, 31, 32, 33, 64, 128, 256, 500];

        [TestCaseSource(nameof(FastHashLengths))]
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
                Assert.That(modifiedHash, Is.Not.EqualTo(originalHash), $"Changing byte at index {i} should change the hash for length {length}");
            }
        }

        [TestCaseSource(nameof(FastHashSeedCases))]
        public void FastHash_SeedAffectsOutput(int length, uint seed1, uint seed2)
        {
            Require(AesIsSupported, "AES path");

            byte[] input = new byte[length];
            for (int i = 0; i < length; i++)
                input[i] = (byte)(i * 0x17 + 0x42);

            Assert.That(FastHashAes(input, seed1), Is.Not.EqualTo(FastHashAes(input, seed2)), $"seeds {seed1} vs {seed2} for {length} bytes");
        }

        private static IEnumerable<TestCaseData> FastHashSeedCases()
        {
            foreach (int length in new int[] { 1, 8, 16, 20, 32, 33, 64, 65, 71, 79, 80, 128 })
                yield return new TestCaseData(length, 1000u, 2000u);

            yield return new TestCaseData(16, 0u, 1u);
            yield return new TestCaseData(16, 0u, uint.MaxValue);
        }

        private static int FastHashAes(byte[] input, uint seed)
        {
            ref byte start = ref MemoryMarshal.GetArrayDataReference(input);
            Vector128<byte> aesSeed = Vector128.Create(seed).AsByte();
            if (input.Length >= 16)
                return SpanExtensions.FastHashAes(ref start, input.Length, aesSeed);

            Vector128<byte> finalSeed = aesSeed ^ Vector128.Create(
                0x9E3779B9u, 0x85EBCA6Bu, 0xC2B2AE35u, 0x27D4EB2Fu).AsByte();
            return SpanExtensions.FastHashAesShort(ref start, input.Length, aesSeed, finalSeed);
        }

#if !ZK_EVM
        [TestCase(false, TestName = "FastHash_StructuredEightByteInputs_AreDistributed_PublicPath")]
        [TestCase(true, TestName = "FastHash_StructuredEightByteInputs_AreDistributed_XxHash3Fallback")]
        public void FastHash_StructuredEightByteInputs_AreDistributed(bool forceXxHash3)
        {
            const int count = 1024;
            const long seed = 0x510E527FADE682D1L;
            ulong pairedDelta = SolveCrcInput(0);
            byte[] input0 = new byte[sizeof(ulong)];
            byte[] input1 = new byte[sizeof(ulong)];
            int equalPairs = 0;

            for (uint value = 0; value < count; value++)
            {
                ulong word = value * 0x9E3779B97F4A7C15UL;
                BinaryPrimitives.WriteUInt64LittleEndian(input0, word);
                BinaryPrimitives.WriteUInt64LittleEndian(input1, word ^ pairedDelta);

                int hash0 = forceXxHash3
                    ? SpanExtensions.FastHashXxHash3(input0, seed)
                    : input0.FastHash();
                int hash1 = forceXxHash3
                    ? SpanExtensions.FastHashXxHash3(input1, seed)
                    : input1.FastHash();
                if (hash0 == hash1) equalPairs++;
            }

            Assert.That(equalPairs, Is.LessThan(4), $"structured pairs produced {equalPairs}/{count} equal hashes");
        }
#endif

        [Test]
        public void FastHash_ShortPaddingIncludesLength()
        {
            byte[] input = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] extended = [1, 2, 3, 4, 5, 6, 7, 8, 0];

            Assert.That(input.FastHash(), Is.Not.EqualTo(extended.FastHash()));
        }

        private const int HashDistributionSampleCount = 4096;
        private static readonly uint[] HashSeeds = [0u, 1u, 0xDEADBEEFu];
        private static bool AesIsSupported => x64.Aes.IsSupported || Arm.Aes.IsSupported;

        [Test]
        public void ComputeAesSeed_LengthAffectsSeed()
            => Assert.That(SpanExtensions.ComputeAesSeed(32), Is.Not.EqualTo(SpanExtensions.ComputeAesSeed(33)));

        [TestCase(32, 0, 4)]
        [TestCase(48, 20, 36)]
        [TestCase(64, 20, 36)]
        [TestCase(192, 68, 132)]
        [TestCase(208, 68, 132)]
        public void FastHashAes_StructuredWordsAreDistributed(int length, int offsetA, int offsetB)
        {
            Require(AesIsSupported, "AES path");

            byte[] input = new byte[length];
            AssertIntHashesAreDistributedForSeeds((value, seed) =>
            {
                BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(offsetA), value);
                BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(offsetB), value);
                return SpanExtensions.FastHashAes(
                    ref MemoryMarshal.GetArrayDataReference(input), length, Vector128.Create(seed).AsByte());
            }, $"{length}-byte structured inputs");
        }

        [Test]
        public void FastHashAes_StructuredFirstRoundInputsAreDistributed()
        {
            Require(AesIsSupported, "AES path");

            byte[] input = new byte[32];
            Vector128<byte> zeroRound = AesRound(Vector128<byte>.Zero);
            AssertIntHashesAreDistributed(value =>
            {
                BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(4), value);
                Vector128<byte> first = MemoryMarshal.Read<Vector128<byte>>(input);
                Vector128<byte> paired = zeroRound ^ AesRound(first);
                MemoryMarshal.Write(input.AsSpan(16), in paired);
                return SpanExtensions.FastHash(input);
            }, "counterbalanced first-round inputs");
        }

        [Test]
        public void StorageCell_GetHashCode_StructuredSlotsAreDistributed()
        {
            Require(AesIsSupported, "AES path");

            byte[] slotBytes = new byte[32];
            AssertIntHashesAreDistributed(value =>
            {
                Vector128<byte> second = Vector128.Create((uint)value, 0u, 0u, 0u).AsByte();
                Vector128<byte> first = AesRound(second);
                MemoryMarshal.Write(slotBytes, in first);
                MemoryMarshal.Write(slotBytes.AsSpan(16), in second);

                StorageCell cell = new(Address.Zero, new UInt256(slotBytes, isBigEndian: false));
                return cell.GetHashCode();
            }, "structured storage slots");
        }

        [Test]
        public void StorageCell_PairedAddressAndSlotHashesAreDistributed()
        {
            Require(AesIsSupported, "AES path");

            const uint lengthSeedDifference = Address.Size ^ 32;
            byte[] addressBytes = new byte[Address.Size];
            byte[] slotBytes = new byte[32];
            int[] intHashes = new int[HashDistributionSampleCount];
            long[] hashes = new long[HashDistributionSampleCount];
            int equalComponentHashes = 0;
            int inconsistentHashWidths = 0;

            for (int value = 0; value < HashDistributionSampleCount; value++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(addressBytes, value);
                for (int offset = 0; offset < 16; offset += sizeof(uint))
                {
                    uint word = BinaryPrimitives.ReadUInt32LittleEndian(addressBytes.AsSpan(offset)) ^ lengthSeedDifference;
                    BinaryPrimitives.WriteUInt32LittleEndian(slotBytes.AsSpan(offset), word);
                }
                addressBytes.AsSpan(16, sizeof(uint)).CopyTo(slotBytes.AsSpan(16));

                Address address = new(addressBytes);
                long indexHash = SpanExtensions.FastHash64For32Bytes(ref MemoryMarshal.GetArrayDataReference(slotBytes));
                equalComponentHashes += indexHash == address.GetHashCode64() ? 1 : 0;

                StorageCell cell = new(address, new UInt256(slotBytes, isBigEndian: false));
                intHashes[value] = cell.GetHashCode();
                hashes[value] = cell.GetHashCode64();
                ulong hash = (ulong)hashes[value];
                inconsistentHashWidths += intHashes[value] != (int)(hash ^ (hash >> 32)) ? 1 : 0;
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(equalComponentHashes, Is.LessThan(4), "fixed-size component hashes");
                Assert.That(inconsistentHashWidths, Is.Zero, "32-bit hashes");
                AssertIntHashesAreDistributed(value => intHashes[value], "paired storage cells");
                AssertHash64WindowsAreDistributed(hashes, "paired storage cells");
            }
        }

        [Test]
        public void MumFold_EqualInputsAreDistributed()
            => AssertIntHashesAreDistributed(
                value => (int)SpanExtensions.MumFold((uint)value, (uint)value),
                "equal input words");

        private static Vector128<byte> AesRound(Vector128<byte> state)
            => x64.Aes.IsSupported
                ? x64.Aes.Encrypt(state, Vector128<byte>.Zero)
                : Arm.Aes.MixColumns(Arm.Aes.Encrypt(state, Vector128<byte>.Zero));

        [Test]
        public void FastHashAes_StructuredTailInputsAreDistributed()
        {
            Require(AesIsSupported, "AES path");

            byte[] input0 = new byte[72];
            byte[] input1 = new byte[72];
            BinaryPrimitives.WriteUInt64LittleEndian(input1.AsSpan(64), SolveCrcInput(0));

            Assert.That(input0.FastHash(), Is.Not.EqualTo(input1.FastHash()));
        }

        [Test]
        public void FastHashAes_Twin16ByteHalvesAreDistributed()
        {
            Require(AesIsSupported, "AES path");

            byte[] input = new byte[32];
            AssertIntHashesAreDistributedForSeeds((value, seed) =>
            {
                BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(4), value);
                BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(20), value);
                return SpanExtensions.FastHashAes(
                    ref MemoryMarshal.GetArrayDataReference(input), input.Length, Vector128.Create(seed).AsByte());
            }, "twin-half inputs");
        }

        // Exercise mirrored values across both pairs of the 4-lane AES fold.
        [Test]
        public void FastHash_PairedSiblingBlocksAreDistributed()
        {
            Require(AesIsSupported, "AES lane fold");

            const int length = 96;       // exercises the 4-lane fold path (> 64 bytes)
            const int blockA = 32;       // 16-byte block feeding one fold-pair lane
            const int blockB = 48;       // 16-byte block feeding its pair
            const int mirrorOffset = 4;  // same offset inside both blocks
            byte[] input = new byte[length];
            AssertIntHashesAreDistributedForSeeds((value, seed) =>
            {
                // Keep the two blocks byte-identical while varying the mirrored value in both.
                BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(blockA + mirrorOffset), value);
                BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(blockB + mirrorOffset), value);
                return SpanExtensions.FastHashAes(
                    ref MemoryMarshal.GetArrayDataReference(input), length, Vector128.Create(seed).AsByte());
            }, "mirrored-block inputs");
        }

        // Exercise correlated values in two words feeding the same CRC lane.
        [Test]
        public void FastHashCrc_StructuredLaneInputsAreDistributed()
        {
            const int length = 64;    // one 64-byte unrolled iteration: words 0 and 32 both feed lane h0
            byte[] input = new byte[length];
            AssertIntHashesAreDistributedForSeeds((value, seed) =>
            {
                ulong delta0 = (uint)value;
                uint target = BitOperations.Crc32C(BitOperations.Crc32C(0u, delta0), 0UL);
                ulong f = SolveCrcInput(target);
                BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(0), delta0);
                BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), f);
                return SpanExtensions.FastHashCrc(
                    ref MemoryMarshal.GetArrayDataReference(input), length, seed);
            }, "CRC intra-lane inputs");
        }

        private static void Require(bool supported, string feature)
        {
            if (!supported)
                Assert.Ignore($"The {feature} is not available on this CPU.");
        }

        private static void AssertIntHashesAreDistributedForSeeds(Func<int, uint, int> getHash, string context)
        {
            foreach (uint seed in HashSeeds)
                AssertIntHashesAreDistributed(value => getHash(value, seed), $"{context} for seed {seed}");
        }

        private static void AssertIntHashesAreDistributed(Func<int, int> getHash, string context)
        {
            HashSet<int> hashes = new(HashDistributionSampleCount);
            for (int value = 0; value < HashDistributionSampleCount; value++)
                hashes.Add(getHash(value));

            Assert.That(hashes.Count, Is.GreaterThan(HashDistributionSampleCount - 32),
                $"{context} collapsed to {hashes.Count}/{HashDistributionSampleCount} distinct hashes");
        }

        // Exercise correlated values across two lanes of the fixed-size CRC path.
        [Test]
        public void FastHash64For32BytesCrc_StructuredLaneInputsAreDistributed()
        {
            foreach (uint seed in HashSeeds)
            {
                byte[] input = new byte[32];
                long[] hashes = new long[HashDistributionSampleCount];
                for (ulong c = 0; c < HashDistributionSampleCount; c++)
                {
                    ulong e = SolveCrcInput(BitOperations.Crc32C(0u, c));
                    BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(0), c);
                    BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16), e);
                    hashes[c] = SpanExtensions.FastHash64For32BytesCrc(
                        ref MemoryMarshal.GetArrayDataReference(input), seed);
                }

                AssertHash64WindowsAreDistributed(hashes, $"32-byte CRC lane inputs for seed {seed}");
            }
        }

        [TestCase(20)]
        [TestCase(32)]
        public void FastHash64_CacheBitWindowsAreDistributed(int length)
        {
            const int count = 4096;
            byte[] input = new byte[length];
            long[] hashes = new long[count];
            long[] crcHashes = new long[count];
#if !ZK_EVM
            long[] xxHashes = new long[count];
#endif
            uint seed = SpanExtensions.ComputeSeed(length);
            for (ulong value = 0; value < count; value++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(length - 8), value);
                ref byte start = ref MemoryMarshal.GetArrayDataReference(input);
                hashes[value] = length == 20
                    ? SpanExtensions.FastHash64For20Bytes(ref start)
                    : SpanExtensions.FastHash64For32Bytes(ref start);
                crcHashes[value] = length == 20
                    ? SpanExtensions.FastHash64For20BytesCrc(ref start, seed)
                    : SpanExtensions.FastHash64For32BytesCrc(ref start, seed);
#if !ZK_EVM
                xxHashes[value] = SpanExtensions.FastHash64XxHash3(ref start, length, seed);
#endif
            }

            AssertHash64WindowsAreDistributed(hashes, $"{length}-byte hashes");
            AssertHash64WindowsAreDistributed(crcHashes, $"{length}-byte CRC hashes");
#if !ZK_EVM
            AssertHash64WindowsAreDistributed(xxHashes, $"{length}-byte XXH3 hashes");
#endif
        }

        private static void AssertHash64WindowsAreDistributed(long[] hashes, string context)
        {
            HashSet<long> fullHashes = new(hashes.Length);
            HashSet<int> way0Sets = new(hashes.Length);
            HashSet<int> signatures = new(hashes.Length);
            HashSet<int> way1Sets = new(hashes.Length);

            foreach (long hash in hashes)
            {
                ulong bits = (ulong)hash;
                fullHashes.Add(hash);
                way0Sets.Add((int)(bits & 0x3FFF));
                signatures.Add((int)((bits >> 22) & 0xF_FFFF));
                way1Sets.Add((int)((bits >> 42) & 0x3FFF));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(fullHashes.Count, Is.GreaterThan(hashes.Length - 32), $"{context}: full hash");
                Assert.That(way0Sets.Count, Is.GreaterThan(hashes.Length * 3 / 4), $"{context}: bits 0-13");
                Assert.That(signatures.Count, Is.GreaterThan(hashes.Length - 32), $"{context}: bits 22-41");
                Assert.That(way1Sets.Count, Is.GreaterThan(hashes.Length * 3 / 4), $"{context}: bits 42-55");
            }
        }

        private static ulong SolveCrcInput(uint target)
        {
            Span<uint> pivBasis = stackalloc uint[32];
            Span<ulong> pivSrc = stackalloc ulong[32];
            pivBasis.Clear();
            ulong dependentInput = 0;
            for (int i = 0; i < 64; i++)
            {
                uint b = BitOperations.Crc32C(0u, 1UL << i);
                ulong s = 1UL << i;
                while (b != 0)
                {
                    int col = BitOperations.TrailingZeroCount(b);
                    if (pivBasis[col] == 0) { pivBasis[col] = b; pivSrc[col] = s; break; }
                    b ^= pivBasis[col];
                    s ^= pivSrc[col];
                }
                if (b == 0 && dependentInput == 0) dependentInput = s;
            }

            if (target == 0) return dependentInput;

            ulong f = 0;
            uint t = target;
            while (t != 0)
            {
                int col = BitOperations.TrailingZeroCount(t);
                if (pivBasis[col] == 0) break; // target not in column space (does not happen for CRC32C)
                t ^= pivBasis[col];
                f ^= pivSrc[col];
            }
            return f;
        }

        // ── CountZeros edge-case tests ──

        /// <summary>
        /// Validates CountZeros against a naive scalar count for sizes that exercise
        /// every codepath boundary: exact multiples of Vector128 (16), Vector256 (32),
        /// Vector512 (64), non-multiples with tails, and sizes below SIMD thresholds.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(15)]
        [TestCase(16)] // Exactly one Vector128
        [TestCase(17)] // One Vector128 + 1-byte scalar tail
        [TestCase(20)] // One Vector128 + 4-byte tail
        [TestCase(24)]
        [TestCase(31)]
        [TestCase(32)] // Exactly two Vector128 (or one Vector256 on x86)
        [TestCase(33)]
        [TestCase(48)] // Exactly three Vector128
        [TestCase(64)] // Exactly four Vector128 (or one Vector512 on x86)
        [TestCase(128)]
        [TestCase(256)]
        [TestCase(1024)]
        public void CountZeros_matches_naive_for_all_sizes(int length)
        {
            Random rng = new(42);
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                int roll = rng.Next(10);
                data[i] = roll < 3 ? (byte)0 : roll < 6 ? (byte)1 : (byte)rng.Next(2, 256);
            }

            int expected = 0;
            foreach (byte t in data)
            {
                if (t == 0) expected++;
            }

            Assert.That(data.AsSpan().CountZeros(), Is.EqualTo(expected));
        }

        /// <summary>
        /// All-zero input at exact vector-width boundaries — every byte should be counted.
        /// Catches off-by-one bugs where the last SIMD chunk is skipped.
        /// </summary>
        [TestCase(16)]
        [TestCase(32)]
        [TestCase(64)]
        [TestCase(128)]
        [TestCase(256)]
        [TestCase(512)]
        public void CountZeros_all_zeros_exact_vector_multiples(int length)
        {
            byte[] data = new byte[length];
            Assert.That(data.AsSpan().CountZeros(), Is.EqualTo(length));
        }

        /// <summary>
        /// All non-zero input — result should be zero. Ensures no false positives
        /// from SIMD comparison logic.
        /// </summary>
        [TestCase(16)]
        [TestCase(32)]
        [TestCase(64)]
        [TestCase(128)]
        [TestCase(256)]
        [TestCase(512)]
        public void CountZeros_no_zeros(int length)
        {
            byte[] data = new byte[length];
            Array.Fill(data, (byte)0xFF);
            Assert.That(data.AsSpan().CountZeros(), Is.EqualTo(0));
        }

        /// <summary>
        /// Single zero at each position in a 32-byte buffer — verifies every lane
        /// of the SIMD comparison is working. On x86 with AVX2, 32 bytes = one
        /// Vector256 iteration; on ARM, two Vector128 iterations.
        /// </summary>
        [Test]
        public void CountZeros_single_zero_per_position()
        {
            for (int pos = 0; pos < 32; pos++)
            {
                byte[] data = new byte[32];
                Array.Fill(data, (byte)0xFF);
                data[pos] = 0;
                Assert.That(data.AsSpan().CountZeros(), Is.EqualTo(1), $"single zero at position {pos} should be counted");
            }
        }
    }
}
