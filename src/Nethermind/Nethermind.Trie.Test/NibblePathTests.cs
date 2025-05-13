// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class NibblePathTests
{
    [TestCase(false, (byte)3, (byte)19)]
    [TestCase(true, (byte)3, (byte)51)]
    public void Encode_gives_correct_output_when_one(bool flag, byte nibble1, byte byte1)
    {
        Span<byte> output = stackalloc byte[1];
        NibblePath.Single(nibble1).EncodeTo(output, flag);
        Assert.That(output[0], Is.EqualTo(byte1));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
    [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
    public void Encode_gives_correct_output_when_odd(bool flag, byte nibble1, byte nibble2, byte nibble3,
        byte byte1, byte byte2)
    {
        NibblePath path = NibblePath.FromNibbles([nibble1, nibble2, nibble3]);
        Span<byte> output = stackalloc byte[2];

        path.EncodeTo(output,flag);

        Assert.That(output[0], Is.EqualTo(byte1));
        Assert.That(output[1], Is.EqualTo(byte2));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
    [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
    public void Encode_gives_correct_output_when_even(bool flag, byte nibble1, byte nibble2, byte byte1, byte byte2)
    {
        NibblePath path = NibblePath.FromNibbles([nibble1, nibble2]);
        Span<byte> output = stackalloc byte[2];

        path.EncodeTo(output,flag);

        Assert.That(output[0], Is.EqualTo(byte1));
        Assert.That(output[1], Is.EqualTo(byte2));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)0, (byte)55)]
    [TestCase(true, (byte)3, (byte)7, (byte)32, (byte)55)]
    public void Decode_gives_correct_output_when_even(bool expectedFlag, byte nibble1, byte nibble2, byte byte1,
        byte byte2)
    {
        (NibblePath key, bool isLeaf) = NibblePath.FromRlpBytes(new[] { byte1, byte2 });
        Assert.That(isLeaf, Is.EqualTo(expectedFlag));
        Assert.That(key.Length, Is.EqualTo(2));
        Assert.That(key[0], Is.EqualTo(nibble1));
        Assert.That(key[1], Is.EqualTo(nibble2));
    }

    [TestCase(false, (byte)3, (byte)19)]
    [TestCase(true, (byte)3, (byte)51)]
    public void Decode_gives_correct_output_when_one(bool expectedFlag, byte nibble1, byte byte1)
    {
        (NibblePath key, bool isLeaf) = NibblePath.FromRlpBytes(new[] { byte1 });

        Assert.That(isLeaf, Is.EqualTo(expectedFlag));
        Assert.That(key.Length, Is.EqualTo(1));
        Assert.That(key[0], Is.EqualTo(nibble1));
    }

    [TestCase(false, (byte)3, (byte)7, (byte)13, (byte)19, (byte)125)]
    [TestCase(true, (byte)3, (byte)7, (byte)13, (byte)51, (byte)125)]
    public void Decode_gives_correct_output_when_odd(bool expectedFlag, byte nibble1, byte nibble2, byte nibble3,
        byte byte1, byte byte2)
    {
        (NibblePath key, bool isLeaf) = NibblePath.FromRlpBytes(new[] { byte1, byte2 });
        Assert.That(isLeaf, Is.EqualTo(expectedFlag));
        Assert.That(key.Length, Is.EqualTo(3));
        Assert.That(key[0], Is.EqualTo(nibble1));
        Assert.That(key[1], Is.EqualTo(nibble2));
        Assert.That(key[2], Is.EqualTo(nibble3));
    }
}
