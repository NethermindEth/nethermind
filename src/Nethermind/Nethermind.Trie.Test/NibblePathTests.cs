// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class NibblePathTests
{
    [TestCase(false, (byte)3, (byte)19)]
    [TestCase(true, (byte)3, (byte)51)]
    public void Encode_gives_correct_output_when_one(bool flag, byte nibble1, byte byte1)
    {
        Span<byte> output = stackalloc byte[1];
        NibblePath.Key.Single(nibble1).EncodeTo(output, flag);
        Assert.That(output[0], Is.EqualTo(byte1));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
    [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
    public void Encode_gives_correct_output_when_odd(bool flag, byte nibble1, byte nibble2, byte nibble3,
        byte byte1, byte byte2)
    {
        var path = NibblePath.Key.FromNibbles([nibble1, nibble2, nibble3]);
        Span<byte> output = stackalloc byte[2];

        path.EncodeTo(output, flag);

        Assert.That(output[0], Is.EqualTo(byte1));
        Assert.That(output[1], Is.EqualTo(byte2));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
    [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
    public void Encode_gives_correct_output_when_even(bool flag, byte nibble1, byte nibble2, byte byte1, byte byte2)
    {
        var path = NibblePath.Key.FromNibbles([nibble1, nibble2]);
        Span<byte> output = stackalloc byte[2];

        path.EncodeTo(output, flag);

        Assert.That(output[0], Is.EqualTo(byte1));
        Assert.That(output[1], Is.EqualTo(byte2));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
    [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
    public void Decode_gives_correct_output_when_even(bool expectedFlag, byte nibble1, byte nibble2, byte byte1,
        byte byte2)
    {
        (NibblePath.Key key, bool isLeaf) = NibblePath.Key.FromRlpBytes(new[] { byte1, byte2 });
        Assert.That(isLeaf, Is.EqualTo(expectedFlag));
        Assert.That(key.Length, Is.EqualTo(2));
        Assert.That(key[0], Is.EqualTo(nibble1));
        Assert.That(key[1], Is.EqualTo(nibble2));
    }

    [TestCase(false, (byte)3, (byte)19)]
    [TestCase(true, (byte)3, (byte)51)]
    public void Decode_gives_correct_output_when_one(bool expectedFlag, byte nibble1, byte byte1)
    {
        (NibblePath.Key key, bool isLeaf) = NibblePath.Key.FromRlpBytes(new[] { byte1 });

        Assert.That(isLeaf, Is.EqualTo(expectedFlag));
        Assert.That(key.Length, Is.EqualTo(1));
        Assert.That(key[0], Is.EqualTo(nibble1));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
    [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
    public void Decode_gives_correct_output_when_odd(bool expectedFlag, byte nibble1, byte nibble2, byte nibble3,
        byte byte1, byte byte2)
    {
        (NibblePath.Key key, bool isLeaf) = NibblePath.Key.FromRlpBytes(new[] { byte1, byte2 });
        Assert.That(isLeaf, Is.EqualTo(expectedFlag));
        Assert.That(key.Length, Is.EqualTo(3));
        Assert.That(key[0], Is.EqualTo(nibble1));
        Assert.That(key[1], Is.EqualTo(nibble2));
        Assert.That(key[2], Is.EqualTo(nibble3));
    }

    [TestCase(new byte[] { 1 })]
    [TestCase(new byte[] { 1, 2 })]
    [TestCase(new byte[] { 1, 2, 3 })]
    [TestCase(new byte[] { 1, 2, 3, 4 })]
    public void Prepend_nibble(byte[] nibbles)
    {
        const byte added = 9;

        var path = NibblePath.Key.FromNibbles(nibbles);

        var prepended = path.PrependWith(added);

        prepended.Length.Should().Be(nibbles.Length + 1);
        prepended[0].Should().Be(added);

        for (int i = 0; i < nibbles.Length; i++)
        {
            prepended[i + 1].Should().Be(nibbles[i]);
        }
    }

    [TestCase(new byte[] { 0xA }, new byte[] { 2, 3 })]
    [TestCase(new byte[] { 0xA, 2 }, new byte[] { 3 })]
    [TestCase(new byte[] { 0xA, 2, 3, 4 }, new byte[] { 5, 6, 7 })]
    [TestCase(new byte[] { 0xA, 2 }, new byte[] { 3, 4 })]
    [TestCase(new byte[] { 0xA }, new byte[] { 6 })]
    [TestCase(new byte[] { 0xA, 2, 3 }, new byte[] { 4, 5, 6 })]
    public void Concat(byte[] a, byte[] b)
    {
        var pathA = NibblePath.Key.FromNibbles(a);
        var pathB = NibblePath.Key.FromNibbles(b);

        var concatenated = pathA.Concat(pathB);

        concatenated.Length.Should().Be(pathA.Length + pathB.Length);

        for (int i = 0; i < a.Length; i++)
        {
            concatenated[i].Should().Be(a[i]);
        }

        for (int i = 0; i < b.Length; i++)
        {
            concatenated[i + a.Length].Should().Be(b[i]);
        }
    }

    [TestCase(new byte[] { 1 }, "0x1")]
    [TestCase(new byte[] { 1, 2 }, "0x12")]
    [TestCase(new byte[] { 1, 2, 3 }, "0x123")]
    [TestCase(new byte[] { 1, 2, 3, 4 }, "0x1234")]
    public void ToHexString(byte[] nibbles, string expected)
    {
        NibblePath.Key.FromNibbles(nibbles).ToHexString().Should().Be(expected);
    }

    private static readonly byte[] OddNibbles = [0xA, 0xB, 0xC, 0xD, 0xE];

    [TestCase(0, 5)]
    [TestCase(1, 4)]
    [TestCase(1, 3)]
    [TestCase(1, 2)]
    [TestCase(1, 1)]
    [TestCase(2, 3)]
    [TestCase(2, 2)]
    [TestCase(2, 1)]
    [TestCase(3, 2)]
    [TestCase(3, 1)]
    [TestCase(4, 1)]
    public void Slice_odd(int start, int length)
    {
        Assert_Slice(start, length, OddNibbles);
    }

    private static readonly byte[] EvenNibbles = [0xA, 0xB, 0xC, 0xD, 0xE, 0xF];

    [TestCase(0, 6)]
    [TestCase(1, 5)]
    [TestCase(1, 4)]
    [TestCase(1, 3)]
    [TestCase(1, 2)]
    [TestCase(1, 1)]
    [TestCase(2, 4)]
    [TestCase(2, 3)]
    [TestCase(2, 2)]
    [TestCase(2, 1)]
    [TestCase(3, 3)]
    [TestCase(3, 2)]
    [TestCase(3, 1)]
    [TestCase(4, 2)]
    [TestCase(4, 1)]
    public void Slice_even(int start, int length)
    {
        Assert_Slice(start, length, EvenNibbles);
    }

    private static void Assert_Slice(int start, int length, byte[] nibbles)
    {
        var expected = NibblePath.Key.FromNibbles(nibbles.AsSpan(start, length));
        var actual = NibblePath.Key.FromNibbles(nibbles).Slice(start, length);

        actual.Equals(expected).Should().BeTrue();
    }

    [TestCase("0x1", new byte[] { 1 })]
    [TestCase("0x12", new byte[] { 1, 2 })]
    [TestCase("0xABC", new byte[] { 0xA, 0xB, 0xC })]
    [TestCase("0xABCD", new byte[] { 0xA, 0xB, 0xC, 0xD })]
    public void FromHexString(string parse, byte[] nibbles)
    {
        var expected = NibblePath.Key.FromNibbles(nibbles);
        var parsed = NibblePath.Key.FromHexString(parse);

        parsed.Equals(expected).Should().BeTrue();
    }

    [Test]
    public void FromNibbles([Range(1, 64)] int length)
    {
        var array = Enumerable.Range(1, length).Select(i => (byte)(i & 15)).ToArray();

        var fromNibbles = NibblePath.Key.FromNibbles(array);
        fromNibbles.Length.Should().Be(length);

        for (int i = 0; i < length; i++)
        {
            fromNibbles[i].Should().Be(array[i]);
        }
    }
}
