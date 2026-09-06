// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Differential tests for the guest variants in <c>Nibbles.zkevm.cs</c>.</summary>
/// <remarks>
/// Those bodies are stripped from every normal build (<c>Directory.Build.targets</c> drops
/// <c>*.zkevm.cs</c> unless <c>EnableZkEvm</c>), so nothing in <c>Nethermind.Trie.Test</c> can reach
/// them. The word-at-a-time forms are compared against the obvious scalar reference over the
/// lengths where their tails and word boundaries land.
/// </remarks>
public class GuestNibblesTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(17)]
    [TestCase(31)]
    [TestCase(32)]
    [TestCase(33)]
    public void Expand_nibbles_matches_the_scalar_reference(int count)
    {
        byte[] bytes = Fill(count, seed: 7);
        byte[] expected = ExpandReference(bytes);
        byte[] actual = new byte[count * 2];

        Nibbles.ExpandNibbles(ref Ref(bytes), ref Ref(actual), count);

        Assert.That(actual, Is.EqualTo(expected));
    }

    /// <remarks>
    /// The SWAR body consumes four bytes at a time, so <c>count % 4</c> selects which scalar tail
    /// runs. Every high nibble is 0x0 and every low nibble 0xF here, which catches a swapped pair
    /// that a symmetric byte value would hide.
    /// </remarks>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    public void Expand_nibbles_orders_the_pair_high_then_low_across_every_tail(int count)
    {
        byte[] bytes = new byte[count];
        Array.Fill(bytes, (byte)0x0F);
        byte[] actual = new byte[count * 2];

        Nibbles.ExpandNibbles(ref Ref(bytes), ref Ref(actual), count);

        for (int i = 0; i < count; i++)
        {
            Assert.That(actual[i * 2], Is.Zero, $"high nibble at {i}");
            Assert.That(actual[(i * 2) + 1], Is.EqualTo(0x0F), $"low nibble at {i}");
        }
    }

    [Test]
    public void Expand_nibbles_writes_nothing_beyond_the_pairs()
    {
        const int count = 6;
        byte[] actual = new byte[(count * 2) + 4];
        Array.Fill(actual, (byte)0xCC);

        Nibbles.ExpandNibbles(ref Ref(Fill(count, seed: 3)), ref Ref(actual), count);

        Assert.That(actual[(count * 2)..], Is.EqualTo(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(16)]
    [TestCase(33)]
    public void Common_prefix_length_matches_the_bcl_when_equal(int length)
    {
        byte[] left = Fill(length, seed: 11);
        byte[] right = (byte[])left.Clone();

        Assert.That(Nibbles.CommonPrefixLength(left, right), Is.EqualTo(length));
        Assert.That(Nibbles.CommonPrefixLength(left, right), Is.EqualTo(((ReadOnlySpan<byte>)left).CommonPrefixLength(right)));
    }

    /// <remarks>
    /// <paramref name="mismatchAt"/> 7/8/15 straddle the 8-byte word boundary the XOR path steps
    /// by, and anything below 8 lands in the scalar tail instead of the word loop.
    /// </remarks>
    [TestCase(20, 0)]
    [TestCase(20, 1)]
    [TestCase(20, 6)]
    [TestCase(20, 7)]
    [TestCase(20, 8)]
    [TestCase(20, 9)]
    [TestCase(20, 15)]
    [TestCase(20, 16)]
    [TestCase(20, 19)]
    [TestCase(7, 0)]
    [TestCase(7, 6)]
    [TestCase(8, 7)]
    public void Common_prefix_length_finds_the_lowest_mismatch(int length, int mismatchAt)
    {
        byte[] left = Fill(length, seed: 5);
        byte[] right = (byte[])left.Clone();
        right[mismatchAt] ^= 0xFF;

        Assert.That(Nibbles.CommonPrefixLength(left, right), Is.EqualTo(mismatchAt));
        Assert.That(Nibbles.CommonPrefixLength(left, right), Is.EqualTo(((ReadOnlySpan<byte>)left).CommonPrefixLength(right)));
    }

    [TestCase(0, 0)]
    [TestCase(0, 5)]
    [TestCase(5, 0)]
    [TestCase(3, 20)]
    [TestCase(20, 3)]
    [TestCase(9, 8)]
    public void Common_prefix_length_stops_at_the_shorter_span(int leftLength, int rightLength)
    {
        byte[] source = Fill(Math.Max(leftLength, rightLength), seed: 13);
        byte[] left = source[..leftLength];
        byte[] right = source[..rightLength];

        Assert.That(Nibbles.CommonPrefixLength(left, right), Is.EqualTo(Math.Min(leftLength, rightLength)));
    }

    private static byte[] Fill(int count, int seed)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            // Deliberately asymmetric nibbles so a high/low swap cannot pass.
            bytes[i] = (byte)((i * 37) + seed);
        }

        return bytes;
    }

    private static byte[] ExpandReference(ReadOnlySpan<byte> bytes)
    {
        byte[] nibbles = new byte[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            nibbles[i * 2] = (byte)(bytes[i] >> 4);
            nibbles[(i * 2) + 1] = (byte)(bytes[i] & 15);
        }

        return nibbles;
    }

    [Test]
    public void Pack_nibbles_matches_the_scalar_reference(
        [Values(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 15, 16, 17, 31, 32, 33)] int count)
    {
        byte[] nibbles = Nibble(count * 2, seed: 3);
        byte[] expected = PackReference(nibbles);
        byte[] actual = new byte[count];

        Nibbles.PackNibbles(ref Ref(nibbles), ref Ref(actual), count);

        Assert.That(actual, Is.EqualTo(expected));
    }

    /// <remarks>
    /// The SWAR body writes four bytes per word read, so <c>count % 4</c> selects which scalar tail
    /// runs. Every high nibble is 0x0 and every low nibble 0xF, which catches a swapped pair that a
    /// symmetric value would hide - the failure a naive lane order produces.
    /// </remarks>
    [Test]
    public void Pack_nibbles_keeps_the_high_nibble_first([Values(1, 2, 3, 4, 5, 7, 8, 9)] int count)
    {
        byte[] nibbles = new byte[count * 2];
        for (int i = 0; i < nibbles.Length; i++) nibbles[i] = (byte)(i % 2 == 0 ? 0x0 : 0xF);
        byte[] actual = new byte[count];

        Nibbles.PackNibbles(ref Ref(nibbles), ref Ref(actual), count);

        Assert.That(actual, Is.EqualTo(PackReference(nibbles)));
        Assert.That(actual, Is.All.EqualTo((byte)0x0F));
    }

    /// <summary>Packing is the exact inverse of expanding, for every length either tail can take.</summary>
    [Test]
    public void Pack_undoes_expand([Values(1, 3, 4, 5, 8, 16, 17, 33)] int count)
    {
        byte[] bytes = Fill(count, seed: 11);
        byte[] nibbles = new byte[count * 2];
        Nibbles.ExpandNibbles(ref Ref(bytes), ref Ref(nibbles), count);
        byte[] roundTripped = new byte[count];

        Nibbles.PackNibbles(ref Ref(nibbles), ref Ref(roundTripped), count);

        Assert.That(roundTripped, Is.EqualTo(bytes));
    }

    /// <remarks>
    /// The pad is a full word wide and the counts straddle <c>count % 4</c>, so a word store that ran
    /// one iteration too far is visible at the counts where it would not land on the boundary.
    /// </remarks>
    [Test]
    public void Pack_nibbles_writes_no_further_than_count([Values(1, 3, 4, 5, 6, 7, 8, 9)] int count)
    {
        byte[] nibbles = Nibble(count * 2, seed: 5);
        byte[] actual = new byte[count + sizeof(uint)];
        Array.Fill(actual, (byte)0xAA);

        Nibbles.PackNibbles(ref Ref(nibbles), ref Ref(actual), count);

        Assert.That(actual[count..], Is.All.EqualTo((byte)0xAA));
    }

    private static byte[] Nibble(int length, int seed)
    {
        byte[] nibbles = new byte[length];
        for (int i = 0; i < length; i++) nibbles[i] = (byte)((i * 7 + seed) & 15);
        return nibbles;
    }

    private static byte[] PackReference(byte[] nibbles)
    {
        byte[] bytes = new byte[nibbles.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((nibbles[i * 2] << 4) | nibbles[(i * 2) + 1]);
        }

        return bytes;
    }

    private static ref byte Ref(byte[] bytes) => ref MemoryMarshal.GetArrayDataReference(bytes);
}
