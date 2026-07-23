// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class TwoLevelBitmapReaderTests
{
    [TestCase(PbtLeafFormat.Legacy)]
    [TestCase(PbtLeafFormat.EveryLevel)]
    [TestCase(PbtLeafFormat.Interleaved)]
    [TestCase(PbtLeafFormat.LeavesOnly)]
    [TestCase(PbtLeafFormat.Every4Depth)]
    public void LeafWrapperPreservesExactFooterAndParsesBackward(PbtLeafFormat format)
    {
        byte[] flat = new byte[TwoLevelBitmapReader.BitmapLength];
        flat[0] = 0x04;
        flat[25] = 0x80;
        byte[] expectedFooter = [0x04, 0x00, 0x00, 0x80, 0x01, 0x10, (byte)format];
        Span<byte> footer = stackalloc byte[CompactBitmap256.MaxEncodedLength + TwoLevelBitmapReader.FormatLength];

        int footerLength = TwoLevelBitmapReader.Encode(flat, footer, format);
        byte[] entriesPrefix = new byte[2 * StemLeafBlob.ValueLength];
        for (int i = 0; i < entriesPrefix.Length; i++) entriesPrefix[i] = (byte)(i + 1);
        byte[] blob = [.. entriesPrefix, .. footer[..footerLength]];

        TwoLevelBitmapReader reader = TwoLevelBitmapReader.FromBlob(blob, out ReadOnlySpan<byte> entries);
        Span<byte> expanded = stackalloc byte[TwoLevelBitmapReader.BitmapLength];
        reader.ExpandTo(expanded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TwoLevelBitmapReader.EncodedLength(flat), Is.EqualTo(expectedFooter.Length));
            Assert.That(footerLength, Is.EqualTo(expectedFooter.Length));
            Assert.That(footer[..footerLength].SequenceEqual(expectedFooter), Is.True, "leaf footer bytes");
            Assert.That(TwoLevelBitmapReader.FormatOf(blob), Is.EqualTo(format));
            Assert.That(entries.SequenceEqual(entriesPrefix), Is.True, "entries region");
            Assert.That(expanded.SequenceEqual(flat), Is.True, "expanded bitmap");
            Assert.That(reader.IsPresent(5), Is.True);
            Assert.That(reader.IsPresent(200), Is.True);
        }
    }

    [TestCase(new byte[] { 0x00, 0x00, 0xff }, "unknown format")]
    [TestCase(new byte[] { 0x04 }, "missing top")]
    [TestCase(new byte[] { 0x01, 0x00, 0x04 }, "missing subword")]
    [TestCase(new byte[] { 0x11, 0x00, 0x00, 0x04 }, "truncated entries")]
    public void FromBlobRejectsMalformedFooter(byte[] blob, string description)
    {
        Assert.That(() => ReadMalformed(blob), Throws.InstanceOf<InvalidDataException>(), description);
    }

    private static void ReadMalformed(byte[] blob) => TwoLevelBitmapReader.FromBlob(blob, out _);
}
