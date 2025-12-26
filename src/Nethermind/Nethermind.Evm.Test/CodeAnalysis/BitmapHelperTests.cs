// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;
using FluentAssertions;

namespace Nethermind.Evm.Test.CodeAnalysis;

[TestFixture]
public class BitmapHelperTests
{
    /// <summary>
    /// A simple reference implementation to check collisions (bitwise AND != 0)
    /// at any corresponding index.
    /// </summary>
    private static bool NaiveCheckCollision(ReadOnlySpan<byte> codeSegments, ReadOnlySpan<byte> jumpMask)
    {
        int length = Math.Min(codeSegments.Length, jumpMask.Length);
        for (int i = 0; i < length; i++)
        {
            if ((codeSegments[i] & jumpMask[i]) != 0)
            {
                return true;
            }
        }
        return false;
    }

    [Test]
    public void CheckCollision_EmptyInputs_ShouldReturnFalse()
    {
        // Both empty
        BitmapHelper.CheckCollision(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty).Should().BeFalse();

        // One empty, one non-empty
        var nonEmpty = new byte[] { 0xFF };
        BitmapHelper.CheckCollision(ReadOnlySpan<byte>.Empty, nonEmpty).Should().BeFalse();
        BitmapHelper.CheckCollision(nonEmpty, ReadOnlySpan<byte>.Empty).Should().BeFalse();
    }

    [Test]
    [TestCase(new byte[] { 0x00 }, new byte[] { 0x00 }, false)]
    [TestCase(new byte[] { 0x00 }, new byte[] { 0xFF }, false)]
    [TestCase(new byte[] { 0xFF }, new byte[] { 0x00 }, false)]
    [TestCase(new byte[] { 0xFF }, new byte[] { 0xFF }, true)]
    [TestCase(new byte[] { 0b0101_0101 }, new byte[] { 0b1010_1010 }, false)]
    [TestCase(new byte[] { 0b0101_0101 }, new byte[] { 0b0001_0100 }, true)]
    public void CheckCollision_SimpleKnownPatterns_ShouldMatchExpected(
        byte[] codeSegmentsArr, byte[] jumpMaskArr, bool expected)
    {
        bool actual = BitmapHelper.CheckCollision(codeSegmentsArr, jumpMaskArr);
        expected.Should().Be(actual,
            $"Expected collision={expected} for code={BitConverter.ToString(codeSegmentsArr)} & jump={BitConverter.ToString(jumpMaskArr)}");
    }

    [Test]
    public void CheckCollision_LastByteCollision_ShouldReturnTrue()
    {
        // The collision only occurs at the last byte
        var codeSegments = new byte[] { 0x00, 0x00, 0x80 };
        var jumpMask = new byte[] { 0x00, 0x00, 0x80 };

        BitmapHelper.CheckCollision(codeSegments, jumpMask).Should().BeTrue("Expected collision in the last byte.");
    }

    [Test]
    public void CheckCollision_FirstByteCollision_ShouldReturnTrue()
    {
        // The collision occurs at the very first byte
        var codeSegments = new byte[] { 0x01, 0x00, 0x00 };
        var jumpMask = new byte[] { 0x01, 0x00, 0x00 };

        BitmapHelper.CheckCollision(codeSegments, jumpMask).Should().BeTrue("Expected collision in the first byte.");
    }

    [Test]
    public void CheckCollision_NoCollision_ShouldReturnFalse()
    {
        var codeSegments = new byte[] { 0xF0, 0xF0, 0xF0, 0xF0 };
        var jumpMask = new byte[] { 0x0F, 0x0F, 0x0F, 0x0F };

        // 0xF0 & 0x0F == 0x00 => no collision
        BitmapHelper.CheckCollision(codeSegments, jumpMask).Should().BeFalse("Expected no collision for complement patterns.");
    }

    [Test]
    public void CheckCollision_DifferentLengths_CollisionShouldBeBasedOnShorter()
    {
        // codeSegments is longer but collision is in the first part
        var codeSegments = new byte[] { 0x00, 0x10, 0x00, 0x10 };
        var jumpMask = new byte[] { 0x00, 0x10 };

        // The second byte in both is 0x10 => 0x10 & 0x10 = 0x10 => collision
        BitmapHelper.CheckCollision(codeSegments, jumpMask).Should().BeTrue("Collision should happen considering the shorter array length.");
    }

    [Test]
    public void CheckCollision_DifferentLengths_NoCollision()
    {
        // codeSegments is shorter; no collision in shorter region
        var codeSegments = new byte[] { 0xF0 };
        var jumpMask = new byte[] { 0x0F, 0xFF, 0xFF };

        // Only the first byte is checked => 0xF0 & 0x0F = 0x00 => no collision
        BitmapHelper.CheckCollision(codeSegments, jumpMask).Should().BeFalse("No collision should happen when shorter region has no overlaps.");
    }

    /// <summary>
    /// A parameterized "fuzz" test that:
    ///  - Generates random byte arrays of various sizes (including large sizes).
    ///  - Checks the result of <see cref="CheckCollision"/> against the naive approach.
    /// </summary>
    /// <param name="length">Size of the array to test.</param>
    [Test]
    [TestCase(1)]
    [TestCase(8)]
    [TestCase(12)]
    [TestCase(16)]
    [TestCase(24)]
    [TestCase(32)]
    [TestCase(48)]
    [TestCase(64)]
    [TestCase(96)]
    [TestCase(128)]
    [TestCase(256)]
    [TestCase(512)]
    public void CheckCollision_Exhaustive(int length)
    {
        var codeSegments = new byte[length];
        var jumpMask = new byte[length];

        for (int i = 0; i < length; i++)
        {
            foreach (byte b in _lookup)
            {
                codeSegments[i] = b;
                TestSegments(codeSegments, jumpMask);
                jumpMask[i] = b;
                TestSegments(codeSegments, jumpMask);
                codeSegments[i] = 0;
                TestSegments(codeSegments, jumpMask);
                jumpMask[i] = 0;
            }
        }

        static void TestSegments(byte[] codeSegments, byte[] jumpMask)
        {
            bool expected = NaiveCheckCollision(codeSegments, jumpMask);
            bool actual = BitmapHelper.CheckCollision(codeSegments, jumpMask);

            expected.Should().Be(actual);
        }
    }

    private static readonly byte[] _lookup =
    {
        0b0000_0000,
        0b0000_0001,
        0b0000_0010,
        0b0000_0100,
        0b0000_1000,
        0b0001_0000,
        0b0010_0000,
        0b0100_0000,
        0b1000_0000
    };
}
