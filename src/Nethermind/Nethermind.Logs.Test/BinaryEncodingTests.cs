// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Logs.Test;

public class BinaryEncodingTests
{
    [TestCase(0u)]
    [TestCase(1u)]
    [TestCase(0xFFu)]
    [TestCase(0xFFFFu)]
    [TestCase(0xFFFFFu)]
    [TestCase(0xFFFFFFu)]
    [TestCase(0xFFFFFFFFu)]
    public void Test(uint value)
    {
        Span<byte> buffer = stackalloc byte[8];

        var written = BinaryEncoding.WriteVarInt(value, buffer);
        BinaryEncoding.TryReadVarInt(buffer, 0, out var read).Should().Be(written);
        read.Should().Be(value);
    }
}
