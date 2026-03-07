// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class UInt256Tests
{
    // Reference: serialize to 32-byte big-endian and count zero bytes.
    private static int ReferenceCountZeroBytes(in UInt256 value)
    {
        Span<byte> bytes = stackalloc byte[32];
        value.ToBigEndian(bytes);
        int count = 0;
        foreach (byte b in bytes)
            if (b == 0) count++;
        return count;
    }

    // Big-endian hex → UInt256
    private static UInt256 From(string hex) =>
        new(Bytes.FromHexString(hex).AsSpan(), isBigEndian: true);

    [TestCase("0000000000000000000000000000000000000000000000000000000000000000", 32)] // all zero
    [TestCase("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", 0)]  // no zeros
    [TestCase("0000000000000000000000000000000000000000000000000000000000000001", 31)] // one in LSB
    [TestCase("0100000000000000000000000000000000000000000000000000000000000000", 31)] // one in MSB byte
    [TestCase("ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00", 16)] // alternating FF,00
    [TestCase("00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff00ff", 16)] // alternating 00,FF
    [TestCase("0101010101010101010101010101010101010101010101010101010101010101", 0)]  // all bytes = 0x01
    [TestCase("abcdef000000000000000000000000000000000000000000000000000000abcd", 27)] // non-zero only at ends
    [TestCase("000000000000000000000000000000000000000000000000000000000000abcd", 30)] // non-zero in last 2 bytes
    [TestCase("abcd000000000000000000000000000000000000000000000000000000000000", 30)] // non-zero in first 2 bytes
    [TestCase("deadbeef00000000000000000000000000000000000000000000000000000000", 28)] // 4 non-zero bytes at start
    [TestCase("00000000000000000000000000000000000000000000000000000000deadbeef", 28)] // 4 non-zero bytes at end
    [TestCase("deadbeef000000000000000000000000000000000000000000000000c0ffee00", 25)] // non-zero at both ends: 4+3 non-zero bytes
    [TestCase("0000000000000000000000000000000000000000000000000000000000000100", 31)] // 0x100 = only byte 0x01 is non-zero
    public void CountZeroBytes_matches_reference_and_expected(string hex, int expectedCount)
    {
        UInt256 value = From(hex);
        int result = value.CountZeroBytes();
        Assert.That(result, Is.EqualTo(expectedCount));
        Assert.That(result, Is.EqualTo(ReferenceCountZeroBytes(value)));
    }

    // Exhaustive: every possible single-byte value (0x00–0xFF) placed at each of the 32 byte positions
    // of a UInt256, with all other bytes zero. Covers 8192 cases and exercises the borrow-propagation
    // edge case for 0x01 bytes within each 64-bit limb.
    [Test]
    public void CountZeroBytes_all_single_byte_values_at_each_position()
    {
        Span<byte> buffer = stackalloc byte[32];
        for (int pos = 0; pos < 32; pos++)
        {
            for (int b = 0; b <= 255; b++)
            {
                buffer.Clear();
                buffer[pos] = (byte)b;
                UInt256 value = new(buffer, isBigEndian: true);
                int result = value.CountZeroBytes();
                int reference = ReferenceCountZeroBytes(in value);
                Assert.That(result, Is.EqualTo(reference),
                    $"byte=0x{b:X2} at big-endian position {pos}");
            }
        }
    }

    // Generates random byte buffers where each byte is independently chosen with 30% probability
    // of 0x00, 30% probability of 0x01, and 40% probability of a value in [2, 255].
    [TestCase(42)]
    [TestCase(123)]
    [TestCase(9999)]
    [TestCase(77777)]
    public void CountZeroBytes_biased_random_matches_reference(int seed)
    {
        var rng = new Random(seed);
        byte[] buffer = new byte[32];
        for (int i = 0; i < 10_000; i++)
        {
            for (int j = 0; j < 32; j++)
            {
                int roll = rng.Next(10);
                buffer[j] = roll < 3 ? (byte)0 : roll < 6 ? (byte)1 : (byte)rng.Next(2, 256);
            }
            UInt256 value = new(buffer.AsSpan(), isBigEndian: true);
            int result = value.CountZeroBytes();
            Assert.That(result, Is.EqualTo(ReferenceCountZeroBytes(in value)),
                $"seed={seed}, iter={i}");
        }
    }

    [Test]
    public void IsOne()
    {
        Assert.That(UInt256.One.IsOne, Is.True, "1");
        Assert.That(UInt256.Zero.IsOne, Is.False, "0");
        Assert.That(((UInt256)BigInteger.Pow(2, 64)).IsOne, Is.False, "2^64");
        Assert.That(((UInt256)BigInteger.Pow(2, 128)).IsOne, Is.False, "2^128");
        Assert.That(((UInt256)BigInteger.Pow(2, 196)).IsOne, Is.False, "2^196");
    }

    [Test]
    public void To_big_endian_can_store_in_address()
    {
        Span<byte> target = stackalloc byte[20];
        UInt256 a = new(Bytes.FromHexString("0xA0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7").AsSpan(), true);
        a.ToBigEndian(target);
        Assert.That(target.ToHexString().ToUpperInvariant(), Is.EqualTo("b4b5b6b7c0c1c2c3c4c5c6c7d0d1d2d3d4d5d6d7".ToUpperInvariant()));
    }

    [Test]
    public void To_big_endian_can_store_on_stack()
    {
        Span<byte> target = stackalloc byte[32];
        UInt256 a = new(Bytes.FromHexString("0xA0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7").AsSpan(), true);
        a.ToBigEndian(target);
        Assert.That(target.ToHexString().ToUpperInvariant(), Is.EqualTo("A0A1A2A3A4A5A6A7B0B1B2B3B4B5B6B7C0C1C2C3C4C5C6C7D0D1D2D3D4D5D6D7".ToUpperInvariant()));
    }
}
