// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class TreePathTests
{
    [Test]
    public void TestAppend()
    {
        TreePath path = CreateFullTreePath();

        string asHex = path.Span.ToHexString();
        Assert.That(asHex, Is.EqualTo("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
    }

    [Test]
    public void TestIndexWrite()
    {
        TreePath path = new(Keccak.Zero, 64);
        for (int i = 0; i < 64; i++)
        {
            path[i] = (byte)(i % 16);
        }

        string asHex = path.Span.ToHexString();
        Assert.That(asHex, Is.EqualTo("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
    }

    [Test]
    public void TestIndexRead()
    {
        TreePath path = new(new ValueHash256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"), 64);
        for (int i = 0; i < 64; i++)
        {
            Assert.That(path[i], Is.EqualTo((byte)(i % 16)));
        }
    }

    [Test]
    public void TestAppendArray()
    {
        byte[] nibbles = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            nibbles[i] = (byte)(i % 16);
        }
        TreePath path = new();
        TreePath newPath = path.Append(nibbles);

        Assert.That(path.Length, Is.EqualTo(0));
        Assert.That(newPath.Length, Is.EqualTo(64));
        string asHex = newPath.Span.ToHexString();
        Assert.That(asHex, Is.EqualTo("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
    }

    [TestCase(1)]
    [TestCase(11)]
    [TestCase(20)]
    [TestCase(40)]
    [TestCase(41)]
    public void TestAppendArrayDivided(int partition)
    {
        byte[] nibbles = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            nibbles[i] = (byte)(i % 16);
        }
        TreePath path = new();
        path = path.Append(nibbles.AsSpan(0, partition));
        Assert.That(path.Length, Is.EqualTo(partition));
        path = path.Append(nibbles.AsSpan(partition));
        Assert.That(path.Length, Is.EqualTo(64));

        string asHex = path.Span.ToHexString();
        Assert.That(asHex, Is.EqualTo("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
    }

    [TestCase(1, 1, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(16, 1, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(30, 1, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(1, 0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(16, 0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(30, 0, "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase(16, 16, "0x0123456789abcdef000000000000000000000000000000000000000000000000")]
    [TestCase(17, 16, "0x0123456789abcdef000000000000000000000000000000000000000000000000")]
    [TestCase(30, 16, "0x0123456789abcdef000000000000000000000000000000000000000000000000")]
    public void TestTruncate(int truncate1, int truncate2, string expectedHash)
    {
        ValueHash256 originalHash = new("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        TreePath path = new(originalHash, 64);
        path = path.Truncate(truncate1);
        Assert.That(path.Length, Is.EqualTo(truncate1));
        path = path.Truncate(truncate2);
        Assert.That(path.Length, Is.EqualTo(truncate2));

        Assert.That(path.Path.ToString(), Is.EqualTo(expectedHash));
    }

    [TestCase("", 0, "0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0x0102", 2, "1200000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0x010203", 3, "1230000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0x01020304", 4, "1234000000000000000000000000000000000000000000000000000000000000")]
    public void TestFromNibble(string nibbleHex, int expectedLength, string expectedHashHex)
    {
        byte[] nibbles = Bytes.FromHexString(nibbleHex);

        TreePath path = TreePath.FromNibble(nibbles);
        Assert.That(path.Length, Is.EqualTo(expectedLength));
        Assert.That(path.Span.ToHexString(), Is.EqualTo(expectedHashHex));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(63)]
    [TestCase(64)]
    public void TestToNibble(int nibbleLength)
    {
        byte[] nibbles = new byte[nibbleLength];
        for (int i = 0; i < nibbles.Length; i++)
        {
            nibbles[i] = (byte)(i & 0x0F);
        }

        TreePath path = TreePath.FromNibble(nibbles);

        byte[] result = path.ToNibble();

        Assert.That(result, Is.EqualTo(nibbles).AsCollection);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(20)]
    [TestCase(63)]
    [TestCase(64)]
    public void TestEncodePathMatchesNibbleSpan(int nibbleLength)
    {
        byte[] nibbles = new byte[nibbleLength];
        for (int i = 0; i < nibbles.Length; i++)
        {
            nibbles[i] = (byte)(i & 0x0F);
        }

        TreePath path = TreePath.FromNibble(nibbles);

        Assert.That(Nibbles.EncodePath(path), Is.EqualTo(Nibbles.EncodePath(nibbles)).AsCollection);
    }

    [TestCase("", "", 0)]
    [TestCase("00", "00", 0)]
    [TestCase("01", "01", 0)]
    [TestCase("01", "02", -1)]
    [TestCase("02", "01", 1)]
    [TestCase("01", "010", -1)]
    [TestCase("010", "01", 1)]
    [TestCase("012", "0120", -1)]
    [TestCase("0120", "012", 1)]
    [TestCase("000102030405060708090a0b0c0d0e", "000102030405060708090a0b0c0d0f", -1)]
    [TestCase("000102030405060708090a0b0c0d0f", "000102030405060708090a0b0c0d0e", 1)]
    public void TestCompareTo(string nibbleHex1, string nibbleHex2, int expectedResult)
    {
        TreePath path1 = TreePath.FromNibble(Bytes.FromHexString(nibbleHex1));
        TreePath path2 = TreePath.FromNibble(Bytes.FromHexString(nibbleHex2));

        if (expectedResult == -1) Assert.That(path1.CompareTo(path2), Is.LessThan(0));
        if (expectedResult == 0) Assert.That(path1.CompareTo(path2), Is.EqualTo(0));
        if (expectedResult == 1) Assert.That(path1.CompareTo(path2), Is.GreaterThan(0));
    }

    [TestCase("0000", 0, "0000", -1)]
    [TestCase("0000", 2, "0000", 0)]
    [TestCase("0001", 0, "0001", -1)]
    [TestCase("0003", 2, "0002", 1)]
    [TestCase("000101", 2, "000100", -1)]
    [TestCase("000101", 3, "000100", 1)]
    [TestCase("000102030405060708090a0b0c0d0e00", 15, "000102030405060708090a0b0c0d0f00", -1)]
    [TestCase("000102030405060708090a0b0c0d0f00", 15, "000102030405060708090a0b0c0d0e00", 1)]
    public void TestCompareToTruncated(string nibbleHex1, int truncateLength, string nibbleHex2, int expectedResult)
    {
        TreePath path1 = TreePath.FromNibble(Bytes.FromHexString(nibbleHex1));
        TreePath path2 = TreePath.FromNibble(Bytes.FromHexString(nibbleHex2));

        if (expectedResult == -1) Assert.That(path1.CompareToTruncated(path2, truncateLength), Is.LessThan(0));
        if (expectedResult == 0) Assert.That(path1.CompareToTruncated(path2, truncateLength), Is.EqualTo(0));
        if (expectedResult == 1) Assert.That(path1.CompareToTruncated(path2, truncateLength), Is.GreaterThan(0));
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
        TreePath path1 = TreePath.FromNibble(nib1);
        TreePath path2 = TreePath.FromNibble(Bytes.FromHexString(nibbleHex2));
        Assert.That(path1.StartsWith(path2), Is.EqualTo(startsWith));
    }

    [TestCase("0000", 1, 0, "0000")]
    [TestCase("0000", 1, 1, "000001")]
    [TestCase("0000", 1, 6, "0000010101010101")]
    public void TestAppend(string nibbleHex1, int nib, int count, string expectedHex)
    {
        TreePath path1 = TreePath.FromNibble(Bytes.FromHexString(nibbleHex1));
        TreePath path2 = TreePath.FromNibble(Bytes.FromHexString(expectedHex));

        Assert.That(path1.Append(nib, count), Is.EqualTo(path2));
    }

    [TestCase]
    public void TestScopedAppend()
    {
        TreePath path = TreePath.Empty;

        using (path.ScopedAppend(new byte[] { 1, 2, 3, 4 }))
        {
            Assert.That(path.Length, Is.EqualTo(4));
            Assert.That(path.Path.ToString(), Is.EqualTo("0x1234000000000000000000000000000000000000000000000000000000000000"));

            using (path.ScopedAppend(new byte[] { 5, 6, 7 }))
            {
                Assert.That(path.Length, Is.EqualTo(7));
                Assert.That(path.Path.ToString(), Is.EqualTo("0x1234567000000000000000000000000000000000000000000000000000000000"));
            }

            Assert.That(path.Length, Is.EqualTo(4));
            Assert.That(path.Path.ToString(), Is.EqualTo("0x1234000000000000000000000000000000000000000000000000000000000000"));
        }
        Assert.That(path.Length, Is.EqualTo(0));
    }

    [TestCase("", "0000000000000000")]
    [TestCase("01", "1000000000000001")]
    [TestCase("000102030405060708", "0123456780000009")]
    [TestCase("000102030405060708090a0b0c0d0e", "0123456789abcdef")] // verifies upper nibble of byte 7 preserved
    [TestCase("000102030405", "0123450000000006")]
    public void TestEncodeWith8Byte(string nibbleHex, string expectedEncodedHex)
    {
        byte[] nibbles = string.IsNullOrEmpty(nibbleHex) ? [] : Bytes.FromHexString(nibbleHex);
        TreePath path = TreePath.FromNibble(nibbles);

        Span<byte> buffer = stackalloc byte[8];
        path.EncodeWith8Byte(buffer);

        Assert.That(buffer.ToArray().ToHexString(), Is.EqualTo(expectedEncodedHex));
    }

    private static TreePath CreateFullTreePath()
    {
        TreePath path = new();
        for (int i = 0; i < 64; i++)
        {
            path = path.Append((byte)(i % 16));
        }

        return path;
    }
}
