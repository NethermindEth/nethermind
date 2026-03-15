// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class UInt64Tests
    {
        [TestCase("7fffffffffffffff", (ulong)long.MaxValue)]
        [TestCase("ffffffffffffffff", ulong.MaxValue)]
        [TestCase("0000", (ulong)0)]
        [TestCase("0001234", (ulong)0x1234)]
        [TestCase("1234", (ulong)0x1234)]
        [TestCase("1", (ulong)1)]
        [TestCase("10", (ulong)16)]
        public void ToLongFromBytes(string hexBytes, ulong expectedValue)
        {
            byte[] bytes = Bytes.FromHexString(hexBytes);
            ulong number = bytes.ToULongFromBigEndianByteArrayWithoutLeadingZeros();
            number.Should().Be(expectedValue);
        }

        // Reference: naive byte-by-byte count, used to cross-validate SWAR result.
        private static int ReferenceCountZeroBytes(ulong value)
        {
            int count = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((value & 0xFF) == 0) count++;
                value >>= 8;
            }
            return count;
        }

        [TestCase((ulong)0, 8)]                      // all 8 bytes are zero
        [TestCase(ulong.MaxValue, 0)]                // no zero bytes
        [TestCase((ulong)1, 7)]                      // only LSB is non-zero
        [TestCase(0x0100000000000000UL, 7)]          // only MSB is non-zero
        [TestCase(0xFF00FF00FF00FF00UL, 4)]          // alternating: FF,00,FF,00,... (4 zero bytes)
        [TestCase(0x00FF00FF00FF00FFUL, 4)]          // alternating: 00,FF,00,FF,... (4 zero bytes)
        [TestCase(0x0101010101010101UL, 0)]          // all bytes = 0x01
        [TestCase(0x8080808080808080UL, 0)]          // all bytes = 0x80
        [TestCase(0xABCDEF0000000000UL, 5)]          // bytes (MSB→LSB): AB,CD,EF,00,00,00,00,00
        [TestCase(0x0000000000ABCDEFUL, 5)]          // bytes (MSB→LSB): 00,00,00,00,00,AB,CD,EF
        [TestCase(0xAB00CD00EF001200UL, 4)]          // 4 zero bytes interleaved
        [TestCase(0xFFFFFFFFFFFFFF00UL, 1)]          // only LSB is zero
        [TestCase(0x00FFFFFFFFFFFFFFUL, 1)]          // only MSB is zero
        [TestCase(0x00FF00000000FF00UL, 6)]          // bytes: 00,FF,00,00,00,00,FF,00 → 6 zeros
        [TestCase(0xDEADBEEF00000000UL, 4)]          // lower 4 bytes all zero
        [TestCase(0x00000000DEADBEEFUL, 4)]          // upper 4 bytes all zero
        public void CountZeroBytes_matches_reference_and_expected(ulong value, int expectedCount)
        {
            int result = value.CountZeroBytes();
            result.Should().Be(expectedCount);
            result.Should().Be(ReferenceCountZeroBytes(value));
        }

        // Exhaustive: every possible single-byte value (0x00–0xFF) placed at each of the 8 byte positions,
        // with all other bytes zero. Covers 2048 cases and directly exercises the borrow-propagation
        // path that caused false positives in the original SWAR formula (byte == 0x01 at position > 0).
        [Test]
        public void CountZeroBytes_all_single_byte_values_at_each_position()
        {
            for (int pos = 0; pos < 8; pos++)
            {
                for (int b = 0; b <= 255; b++)
                {
                    ulong value = (ulong)(byte)b << (pos * 8);
                    int result = value.CountZeroBytes();
                    int reference = ReferenceCountZeroBytes(value);
                    result.Should().Be(reference, $"byte=0x{b:X2} at bit-position {pos * 8}");
                }
            }
        }

        // Generates random ulong values where each byte is independently chosen with 30% probability
        // of 0x00, 30% probability of 0x01, and 40% probability of a value in [2, 255].
        // This bias directly targets the borrow-propagation edge case of SWAR algorithms.
        private static ulong NextBiasedUInt64(Random rng)
        {
            ulong value = 0;
            for (int pos = 0; pos < 8; pos++)
            {
                int roll = rng.Next(10);
                byte b = roll < 3 ? (byte)0 : roll < 6 ? (byte)1 : (byte)rng.Next(2, 256);
                value |= (ulong)b << (pos * 8);
            }
            return value;
        }

        [TestCase(42)]
        [TestCase(123)]
        [TestCase(9999)]
        [TestCase(77777)]
        public void CountZeroBytes_biased_random_matches_reference(int seed)
        {
            var rng = new Random(seed);
            for (int i = 0; i < 10_000; i++)
            {
                ulong value = NextBiasedUInt64(rng);
                int result = value.CountZeroBytes();
                result.Should().Be(ReferenceCountZeroBytes(value),
                    $"seed={seed}, iter={i}, value=0x{value:X16}");
            }
        }
    }
}
