// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class TwoLevelBitmapReaderTests
{
    private static readonly int[][] LeafSets =
    [
        [],
        [0],
        [255],
        [5, 200],
        [0, 1, 2, 3, 4],
        [7, 8, 15, 16, 128, 129, 240, 255],
        FullRange(),
    ];

    [TestCaseSource(nameof(LeafSets))]
    public void EncodeRoundTripsPresenceExpandAndLength(int[] present)
    {
        Span<byte> flat = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        flat.Clear();
        foreach (int subIndex in present) flat[subIndex >> 3] |= (byte)(1 << (7 - (subIndex & 7)));

        int expectedGroups = OccupiedGroups(flat);

        Span<byte> footer = stackalloc byte[TwoLevelBitmapReader.BitmapLength + 3];
        int footerLength = TwoLevelBitmapReader.Encode(flat, footer, PbtLeafFormat.Interleaved);
        Assert.That(footerLength, Is.EqualTo(expectedGroups * 2 + 2 + 1));
        Assert.That(TwoLevelBitmapReader.FormatOf(footer[..footerLength]), Is.EqualTo(PbtLeafFormat.Interleaved));

        // Prepend a dummy entries region to exercise footer-from-the-tail parsing.
        byte[] entriesPrefix = new byte[64];
        for (int i = 0; i < entriesPrefix.Length; i++) entriesPrefix[i] = (byte)(i + 1);
        byte[] blob = [.. entriesPrefix, .. footer[..footerLength]];

        TwoLevelBitmapReader reader = TwoLevelBitmapReader.FromBlob(blob, out ReadOnlySpan<byte> entries);

        Span<byte> expanded = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        reader.ExpandTo(expanded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.SequenceEqual(entriesPrefix), Is.True, "entries region");
            Assert.That(reader.OccupiedGroups, Is.EqualTo(expectedGroups));
            Assert.That(expanded.SequenceEqual(flat), Is.True, "expanded bitmap");

            for (int subIndex = 0; subIndex < 256; subIndex++)
            {
                bool expected = (flat[subIndex >> 3] & (1 << (7 - (subIndex & 7)))) != 0;
                Assert.That(reader.IsPresent((byte)subIndex), Is.EqualTo(expected), $"sub-index {subIndex}");
            }
        }
    }

    [Test]
    public void FromBlobRejectsUnknownFormatByte()
    {
        byte[] blob = [0x00, 0x00, 0xFF];
        Assert.That(() => TwoLevelBitmapReader.FromBlob(blob, out _), Throws.InstanceOf<InvalidDataException>());
    }

    private static int OccupiedGroups(ReadOnlySpan<byte> flat)
    {
        int count = 0;
        for (int g = 0; g < TwoLevelBitmapReader.GroupCount; g++)
            if ((flat[2 * g] | flat[2 * g + 1]) != 0) count++;
        return count;
    }

    private static int[] FullRange()
    {
        int[] all = new int[256];
        for (int i = 0; i < all.Length; i++) all[i] = i;
        return all;
    }
}
