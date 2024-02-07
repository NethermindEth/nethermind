// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class TreePathTests
{
    [Test]
    public void TestAppend()
    {
        TreePath path = CreateFullTreePath();

        string asHex = path.Path.Bytes.ToHexString();
        asHex.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }

    [Test]
    public void TestIndexWrite()
    {
        TreePath path = CreateFullTreePath();

        string asHex = path.Path.Bytes.ToHexString();
        asHex.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }

    [Test]
    public void TestIndexRead()
    {
        TreePath path = new TreePath(new ValueHash256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"), 64);
        for (int i = 0; i < 64; i++)
        {
            path[i].Should().Be((byte)(i % 16));
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
        TreePath path = new TreePath();
        TreePath newPath = path.Append(nibbles);

        path.Length.Should().Be(0);
        newPath.Length.Should().Be(64);
        string asHex = newPath.Path.Bytes.ToHexString();
        asHex.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
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
        TreePath path = new TreePath();
        path = path.Append(nibbles[..partition]);
        path.Length.Should().Be(partition);
        path = path.Append(nibbles[partition..]);
        path.Length.Should().Be(64);

        string asHex = path.Path.Bytes.ToHexString();
        asHex.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }

    [TestCase(1)]
    [TestCase(11)]
    [TestCase(20)]
    [TestCase(40)]
    [TestCase(41)]
    public void TestAppendArrayDividedWithAnotherTreePath(int partition)
    {
        byte[] nibbles = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            nibbles[i] = (byte)(i % 16);
        }
        TreePath path = new TreePath();
        path.AppendMut(TreePath.FromNibble(nibbles[..partition]));
        path.Length.Should().Be(partition);
        path.AppendMut(TreePath.FromNibble(nibbles[partition..]));
        path.Length.Should().Be(64);

        string asHex = path.Path.Bytes.ToHexString();
        asHex.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
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
        ValueHash256 originalHash = new ValueHash256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        TreePath path = new TreePath(originalHash, 64);
        path = path.Truncate(truncate1);
        path.Length.Should().Be(truncate1);
        path = path.Truncate(truncate2);
        path.Length.Should().Be(truncate2);

        path.Path.ToString().Should().Be(expectedHash);
    }

    [TestCase("", 0, "0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0x0102", 2, "1200000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0x010203", 3, "1230000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0x01020304", 4, "1234000000000000000000000000000000000000000000000000000000000000")]
    public void TestFromNibble(string nibbleHex, int expectedLength, string expectedHashHex)
    {
        byte[] nibbles = Bytes.FromHexString(nibbleHex);

        TreePath path = TreePath.FromNibble(nibbles);
        path.Length.Should().Be(expectedLength);
        path.Path.BytesAsSpan.ToHexString().Should().Be(expectedHashHex);
    }

    [TestCase]
    public void TestScopedAppend()
    {
        TreePath path = TreePath.Empty;

        using (path.ScopedAppend(new byte[] { 1, 2, 3, 4 }))
        {
            path.Length.Should().Be(4);
            path.Path.ToString().Should().Be("0x1234000000000000000000000000000000000000000000000000000000000000");

            using (path.ScopedAppend(new byte[] { 5, 6, 7 }))
            {
                path.Length.Should().Be(7);
                path.Path.ToString().Should().Be("0x1234567000000000000000000000000000000000000000000000000000000000");
            }

            path.Length.Should().Be(4);
            path.Path.ToString().Should().Be("0x1234000000000000000000000000000000000000000000000000000000000000");
        }
        path.Length.Should().Be(0);
    }

    [TestCase]
    public void TestRangeIndex()
    {
        TreePath path = CreateFullTreePath();

        for (int startI = 0; startI < 64; startI++)
        {
            for (int length = 0; length < 64-startI; length++)
            {
                TreePath indexed = path[startI..(startI+length)];

                indexed.Length.Should().Be(length);

                for (int i = 0; i < length; i++)
                {
                    int offsetI = i + startI;
                    indexed[i].Should().Be((byte)(offsetI % 16));
                }
            }
        }
    }

    [TestCase]
    public void TestCommonPrefixLength()
    {
        TreePath path = TreePath.FromHexString("1111111111111111111111111111111111111111111111111111111111111111");

        for (int commonLength = 0; commonLength < 64; commonLength++)
        {
            TreePath another = TreePath.FromHexString(string.Concat(Enumerable.Repeat('1', commonLength)));

            path.CommonPrefixLength(another).Should().Be(commonLength);
            another.CommonPrefixLength(path).Should().Be(commonLength);
        }
    }

    private static TreePath CreateFullTreePath()
    {
        TreePath path = new TreePath();
        for (int i = 0; i < 64; i++)
        {
            path = path.Append((byte)(i % 16));
        }

        return path;
    }
}
