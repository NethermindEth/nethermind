// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class TreePathTests
{
    [Test]
    public void TestAppend()
    {
        TreePath path = new TreePath();
        for (int i = 0; i < 64; i++)
        {
            path = path.Append((byte)(i % 16));
        }

        string asHex = path.Path.Bytes.ToHexString();
        asHex.Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }

    [Test]
    public void TestIndexWrite()
    {
        TreePath path = new TreePath(Keccak.Zero, 64);
        for (int i = 0; i < 64; i++)
        {
            path[i] = (byte)(i % 16);
        }

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
}
