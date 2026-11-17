// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class NodeRlpReaderTests
{
    // A one-item list of a 0x38-byte string, so both prefixes are long.
    private static readonly byte[] LongItem = Bytes.FromHexString("f83ab838" + new string('1', 0x38 * 2));

    [TestCase("c3820102", 0, 4)]
    [TestCase("c3820102", 1, 3)]
    [TestCase("01", 0, 1)]
    [TestCase("80", 0, 1)]
    public void PeekNextRlpLength_reads_short_items(string hex, int position, int expected) =>
        Assert.That(new NodeRlpReader(Bytes.FromHexString(hex)).PeekNextRlpLength(position), Is.EqualTo(expected));

    [Test]
    public void Reads_long_prefixes()
    {
        NodeRlpReader reader = new(LongItem);
        int position = 0;
        reader.ReadSequenceLength(ref position, out int contentLength);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((position, contentLength), Is.EqualTo((2, 0x3a)));
            Assert.That(reader.PeekNextRlpLength(0), Is.EqualTo(LongItem.Length));
            Assert.That(reader.PeekPrefixAndContentLength(2), Is.EqualTo((2, 0x38)));
        }

        reader.DecodeByteArraySpan(ref position, out ReadOnlySpan<byte> value);
        Assert.That((position, value.Length), Is.EqualTo((LongItem.Length, 0x38)));
    }

    [TestCase("f83a")]
    [TestCase("b838")]
    [TestCase("f9")]
    [TestCase("b8")]
    public void Truncated_long_item_throws(string hex)
    {
        byte[] data = Bytes.FromHexString(hex);
        Assert.Throws<RlpReaderException>(() => new NodeRlpReader(data).PeekNextRlpLength(0));
        Assert.Throws<RlpReaderException>(() =>
        {
            int position = 0;
            new NodeRlpReader(data).SkipItems(ref position, 1);
        });
    }

    [TestCase("b801" + "01")]
    [TestCase("f801" + "01")]
    [TestCase("f800" + "01")]
    public void Long_prefix_with_a_short_length_throws(string hex)
    {
        byte[] data = Bytes.FromHexString(hex);
        Assert.Throws<RlpReaderException>(() => new NodeRlpReader(data).PeekNextRlpLength(0));
        Assert.Throws<RlpReaderException>(() =>
        {
            int position = 0;
            new NodeRlpReader(data).ReadSequenceLength(ref position, out _);
        });
    }

    [TestCase("c3820102", 1)]
    [TestCase("f83a", 2)]
    [TestCase("b838", 2)]
    [TestCase("f9", 3)]
    [TestCase("01", 0)]
    public void SkipLength_moves_past_the_prefix_without_reading_it(string hex, int expected)
    {
        int position = 0;
        new NodeRlpReader(Bytes.FromHexString(hex)).SkipLength(ref position);
        Assert.That(position, Is.EqualTo(expected));
    }

    /// <remarks>What <c>TrieNode.TryDecodeRlp</c> catches for a short item that is cut off.</remarks>
    [Test]
    public void Truncated_short_item_fails_the_slice()
    {
        byte[] data = Bytes.FromHexString("a0" + new string('1', 62));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            int position = 0;
            new NodeRlpReader(data).DecodeValueKeccakNonNull(ref position, out ValueHash256 _);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            int position = 0;
            new NodeRlpReader(data).DecodeByteArraySpan(ref position, out ReadOnlySpan<byte> _);
        });
    }
}
