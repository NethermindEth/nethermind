// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Verkle.Tree.Test;

public class VerklePathTests
{
    [Test]
    public void TestAppend()
    {
        VerklePath path = CreateFullVerklePath();

        string asHex = path.Span.ToHexString();
        asHex.Should().Be("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
    }

    [Test]
    public void TestIndexWrite()
    {
        VerklePath path = new VerklePath(Keccak.Zero, 32);
        for (byte i = 0; i < 32; i++)
        {
            path[i] = i;
        }

        string asHex = path.Span.ToHexString();
        asHex.Should().Be("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
    }

    [Test]
    public void TestIndexRead()
    {
        VerklePath path = new VerklePath(new ValueHash256("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"), 32);
        for (byte i = 0; i < 32; i++)
        {
            path[i].Should().Be(i);
        }
    }

    [Test]
    public void TestAppendArray()
    {
        byte[] nibbles = new byte[32];
        for (byte i = 0; i < 32; i++)
        {
            nibbles[i] = i;
        }
        VerklePath path = new VerklePath();
        VerklePath newPath = path.Append(nibbles);

        path.Length.Should().Be(0);
        newPath.Length.Should().Be(32);
        string asHex = newPath.Span.ToHexString();
        asHex.Should().Be("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
    }

    [TestCase(1)]
    [TestCase(5)]
    [TestCase(10)]
    [TestCase(20)]
    [TestCase(21)]
    public void TestAppendArrayDivided(int partition)
    {
        byte[] nibbles = new byte[32];
        for (byte i = 0; i < 32; i++)
        {
            nibbles[i] = i;
        }
        VerklePath path = new VerklePath();
        path = path.Append(nibbles[..partition]);
        path.Length.Should().Be(partition);
        path = path.Append(nibbles[partition..]);
        path.Length.Should().Be(32);

        string asHex = path.Span.ToHexString();
        asHex.Should().Be("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
    }

    [TestCase(1, 1, "0x0100000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(16, 1, "0x0100000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(30, 1, "0x0100000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(1, 0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(16, 0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(30, 0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(16, 8, "0x0123456789abcdef000000000000000000000000000000000000000000000000")]
    [TestCase(17, 8, "0x0123456789abcdef000000000000000000000000000000000000000000000000")]
    [TestCase(30, 8, "0x0123456789abcdef000000000000000000000000000000000000000000000000")]
    public void TestTruncate(int truncate1, int truncate2, string expectedHash)
    {
        ValueHash256 originalHash = new ValueHash256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        VerklePath path = new VerklePath(originalHash, 32);
        path = path.Truncate(truncate1);
        path.Length.Should().Be(truncate1);
        path = path.Truncate(truncate2);
        path.Length.Should().Be(truncate2);

        path.Path.ToString().Should().Be(expectedHash);
    }

    [TestCase("", "", 0)]
    [TestCase("00", "00", 0)]
    [TestCase("01", "01", 0)]
    [TestCase("01", "02", -1)]
    [TestCase("02", "01", 1)]
    [TestCase("01", "0100", -1)]
    [TestCase("0100", "01", 1)]
    [TestCase("0120", "0120", 0)]
    [TestCase("0120", "0120", 0)]
    public void TestCompareTo(string nibbleHex1, string nibbleHex2, int expectedResult)
    {
        VerklePath path1 = VerklePath.FromPath(Bytes.FromHexString(nibbleHex1));
        VerklePath path2 = VerklePath.FromPath(Bytes.FromHexString(nibbleHex2));

        if (expectedResult == -1) path1.CompareTo(path2).Should().BeLessThan(0);
        if (expectedResult == 0) path1.CompareTo(path2).Should().Be(0);
        if (expectedResult == 1) path1.CompareTo(path2).Should().BeGreaterThan(0);
    }

    [TestCase("0000", 0, "0000", -1)]
    [TestCase("0000", 2, "0000", 0)]
    [TestCase("0001", 0, "0001", -1)]
    [TestCase("0003", 2, "0002", 1)]
    [TestCase("000101", 2, "000100", -1)]
    [TestCase("000101", 3, "000100", 1)]
    public void TestCompareToTruncated(string nibbleHex1, int truncateLength, string nibbleHex2, int expectedResult)
    {
        VerklePath path1 = VerklePath.FromPath(Bytes.FromHexString(nibbleHex1));
        VerklePath path2 = VerklePath.FromPath(Bytes.FromHexString(nibbleHex2));

        if (expectedResult == -1) path1.CompareToTruncated(path2, truncateLength).Should().BeLessThan(0);
        if (expectedResult == 0) path1.CompareToTruncated(path2, truncateLength).Should().Be(0);
        if (expectedResult == 1) path1.CompareToTruncated(path2, truncateLength).Should().BeGreaterThan(0);
    }

    [TestCase("0000", "0000", true)]
    [TestCase("0001", "0000", false)]
    [TestCase("0001", "00", true)]
    [TestCase("0001", "", true)]
    [TestCase("0101", "1", true)]
    [TestCase("0101", "0", false)]
    public void TestStartsWith(string nibbleHex1, string nibbleHex2, bool startsWith)
    {
        byte[] nib1 = Bytes.FromHexString(nibbleHex1);
        VerklePath path1 = VerklePath.FromPath(nib1);
        VerklePath path2 = VerklePath.FromPath(Bytes.FromHexString(nibbleHex2));
        path1.StartsWith(path2).Should().Be(startsWith);
    }

    [TestCase("0000", 1, 0, "0000")]
    [TestCase("0000", 1, 1, "000001")]
    [TestCase("0000", 1, 6, "0000010101010101")]
    public void TestAppend(string nibbleHex1, byte nib, int count, string expectedHex)
    {
        VerklePath path1 = VerklePath.FromPath(Bytes.FromHexString(nibbleHex1));
        VerklePath path2 = VerklePath.FromPath(Bytes.FromHexString(expectedHex));

        path1.Append(nib, count).Should().Be(path2);
    }

    [TestCase]
    public void TestScopedAppend()
    {
        VerklePath path = VerklePath.Empty;

        using (path.ScopedAppend(new byte[] { 1, 2, 3, 4 }))
        {
            path.Length.Should().Be(4);
            path.Path.ToString().Should().Be("0x0102030400000000000000000000000000000000000000000000000000000000");

            using (path.ScopedAppend(new byte[] { 5, 6, 7 }))
            {
                path.Length.Should().Be(7);
                path.Path.ToString().Should().Be("0x0102030405060700000000000000000000000000000000000000000000000000");
            }

            path.Length.Should().Be(4);
            path.Path.ToString().Should().Be("0x0102030400000000000000000000000000000000000000000000000000000000");
        }
        path.Length.Should().Be(0);
    }

    private static VerklePath CreateFullVerklePath()
    {
        VerklePath path = new VerklePath();
        for (byte i = 0; i < 32; i++)
        {
            path = path.Append(i);
        }

        return path;
    }
}
