// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Differential tests for <see cref="PbtKeyDerivation.CopyBits"/>, which the byte-at-a-time
/// implementation must match bit for bit — it places the address prefix and suffix of every
/// storage stem, so a single misplaced bit silently changes the state root.
/// </summary>
public class PbtCopyBitsTests
{
    /// <summary>The obvious bit-at-a-time implementation, kept here as the oracle.</summary>
    private static void CopyBitsReference(ReadOnlySpan<byte> src, int bitCount, Span<byte> dest, int destBitOffset)
    {
        for (int i = 0; i < bitCount; i++)
        {
            if (((src[i >> 3] >> (7 - (i & 7))) & 1) != 0)
            {
                int destBit = destBitOffset + i;
                dest[destBit >> 3] |= (byte)(1 << (7 - (destBit & 7)));
            }
        }
    }

    [Test]
    public void MatchesReferenceAcrossOffsetsAndLengths()
    {
        Random random = new(42);
        byte[] src = new byte[Stem.Length + 1];

        for (int trial = 0; trial < 32; trial++)
        {
            random.NextBytes(src);

            for (int destBitOffset = 0; destBitOffset <= 16; destBitOffset++)
            {
                for (int bitCount = 0; bitCount + destBitOffset <= Stem.Length * 8; bitCount++)
                {
                    byte[] actual = new byte[Stem.Length];
                    byte[] expected = new byte[Stem.Length];

                    PbtKeyDerivation.CopyBits(src, bitCount, actual, destBitOffset);
                    CopyBitsReference(src, bitCount, expected, destBitOffset);

                    Assert.That(actual, Is.EqualTo(expected), $"trial {trial}, offset {destBitOffset}, count {bitCount}");
                }
            }
        }
    }

    /// <summary>The exact shapes <see cref="PbtKeyDerivation.StorageStem"/> issues: the 60-bit address prefix at bit 1, then the 187-bit suffix at bit 61, filling the stem to its last bit.</summary>
    [TestCase(60, 1)]
    [TestCase(187, 61)]
    public void MatchesReferenceForStorageStemShapes(int bitCount, int destBitOffset)
    {
        Random random = new(7);
        byte[] src = new byte[32];

        for (int trial = 0; trial < 256; trial++)
        {
            random.NextBytes(src);
            byte[] actual = new byte[Stem.Length];
            byte[] expected = new byte[Stem.Length];

            PbtKeyDerivation.CopyBits(src, bitCount, actual, destBitOffset);
            CopyBitsReference(src, bitCount, expected, destBitOffset);

            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    /// <summary>The two storage-stem copies share a byte, so CopyBits must OR into the destination, not overwrite it.</summary>
    [Test]
    public void PreservesBitsAlreadySet()
    {
        byte[] src = [0xFF, 0xFF, 0xFF, 0xFF];
        byte[] actual = new byte[Stem.Length];
        byte[] expected = new byte[Stem.Length];
        actual[0] = expected[0] = 0x80;

        PbtKeyDerivation.CopyBits(src, 20, actual, 1);
        CopyBitsReference(src, 20, expected, 1);

        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(actual[0] & 0x80, Is.EqualTo(0x80));
    }
}
