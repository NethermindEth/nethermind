// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class EncodingExtensionsTests
{
    [Test]
    // 1-bytes chars
    [TestCase("1234567890", 1, "1")]
    [TestCase("1234567890", 5, "12345")]
    [TestCase("1234567890", 10, "1234567890")]
    [TestCase("1234567890", 20, "1234567890")]
    // JSON
    [TestCase(
        """{"id":1,"jsonrpc":"2.0","method":"eth_blockNumber","params":[]}""",
        10, """{"id":1,"j"""
    )]
    [TestCase("""{"id":1,"jsonrpc":"2.0","method":"eth_blockNumber","params":[]}""",
        63, """{"id":1,"jsonrpc":"2.0","method":"eth_blockNumber","params":[]}"""
    )]
    [TestCase(
        """{"id":1,"jsonrpc":"2.0","method":"eth_blockNumber","params":[]}""",
        64, """{"id":1,"jsonrpc":"2.0","method":"eth_blockNumber","params":[]}"""
    )]
    // 2-bytes chars
    [TestCase("\u0101\u0102\u0103\u0104\u0105", 1, "\u0101")]
    [TestCase("\u0101\u0102\u0103\u0104\u0105", 3, "\u0101\u0102\u0103")]
    [TestCase("\u0101\u0102\u0103\u0104\u0105", 5, "\u0101\u0102\u0103\u0104\u0105")]
    [TestCase("\u0101\u0102\u0103\u0104\u0105", 10, "\u0101\u0102\u0103\u0104\u0105")]
    public void TryGetStringSlice_Utf8_SingleSegment(string text, int charsLimit, string expected)
    {
        System.Text.Encoding encoding = System.Text.Encoding.UTF8;
        var sequence = new ReadOnlySequence<byte>(encoding.GetBytes(text));

        encoding.TryGetStringSlice(sequence, charsLimit, out var completed, out var result).Should().BeTrue();

        result.Should().Be(expected);
        completed.Should().Be(charsLimit >= text.Length);
    }
}
