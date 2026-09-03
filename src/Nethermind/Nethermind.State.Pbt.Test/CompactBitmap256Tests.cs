// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class CompactBitmap256Tests
{
    private static IEnumerable<TestCaseData> Bitmaps()
    {
        yield return Case("Empty", [], [0x00, 0x00]);
        yield return Case("BoundaryBits", [0, 15, 16, 255], [0x80, 0x01, 0x80, 0x00, 0x00, 0x01, 0x03, 0x80]);
        yield return Case("Sparse", [5, 200], [0x04, 0x00, 0x00, 0x80, 0x01, 0x10]);
        yield return Case("Dense", DenseBits(), [.. DenseSubwords(), 0xff, 0xff]);
        yield return Case("Full", FullRange(), [.. FullSubwords(), 0xff, 0xff]);
    }

    [TestCaseSource(nameof(Bitmaps))]
    public void ByteAndWordFormsWriteExactBytesAndRoundTrip(int[] setBits, byte[] expected)
    {
        byte[] flat = Flat(setBits);
        ulong[] words = Words(setBits);
        Span<byte> fromBytes = stackalloc byte[CompactBitmap256.MaxEncodedLength];
        Span<byte> fromWords = stackalloc byte[CompactBitmap256.MaxEncodedLength];

        int bytesLength = CompactBitmap256.Write(flat, fromBytes);
        int wordsLength = CompactBitmap256.Write(words, fromWords);
        CompactBitmap256 bitmap = CompactBitmap256.Read(fromBytes[..bytesLength]);
        Span<byte> expanded = stackalloc byte[CompactBitmap256.FlatLength];
        expanded.Fill(0xff);
        bitmap.ExpandTo(expanded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CompactBitmap256.EncodedLength(flat), Is.EqualTo(expected.Length), "byte-form encoded length");
            Assert.That(CompactBitmap256.EncodedLength(words), Is.EqualTo(expected.Length), "word-form encoded length");
            Assert.That(bytesLength, Is.EqualTo(expected.Length), "byte-form write length");
            Assert.That(wordsLength, Is.EqualTo(expected.Length), "word-form write length");
            Assert.That(bitmap.Length, Is.EqualTo(expected.Length), "reader encoded length");
            Assert.That(bitmap.OccupiedGroups, Is.EqualTo((expected.Length - CompactBitmap256.TopLength) / CompactBitmap256.SubwordLength));
            Assert.That(fromBytes[..bytesLength].SequenceEqual(expected), Is.True, "byte-form encoding");
            Assert.That(fromWords[..wordsLength].SequenceEqual(expected), Is.True, "word-form encoding");
            Assert.That(expanded.SequenceEqual(flat), Is.True, "expanded bitmap");
            for (int bit = 0; bit < CompactBitmap256.BitCount; bit++)
            {
                Assert.That(bitmap.IsSet((byte)bit), Is.EqualTo(Array.BinarySearch(setBits, bit) >= 0), $"bit {bit}");
            }
        }
    }

    [TestCaseSource(nameof(Bitmaps))]
    public void ReadsBackwardAndLeavesPrefix(int[] setBits, byte[] encoded)
    {
        byte[] prefix = [0x11, 0x22, 0x33];
        byte[] source = [.. prefix, .. encoded];

        CompactBitmap256 bitmap = CompactBitmap256.ReadFromEnd(source, out ReadOnlySpan<byte> actualPrefix);
        Span<byte> expanded = stackalloc byte[CompactBitmap256.FlatLength];
        bitmap.ExpandTo(expanded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualPrefix.SequenceEqual(prefix), Is.True);
            Assert.That(expanded.SequenceEqual(Flat(setBits)), Is.True);
        }
    }

    [TestCaseSource(nameof(MalformedEncodings))]
    public void RejectsMalformedExactAndTruncatedBackwardEncodings(byte[] encoded, bool backward)
    {
        Assert.That(TryRead(encoded), Is.False);
        Assert.That(() => ReadMalformed(encoded, backward), Throws.InstanceOf<InvalidDataException>());
    }

    private static IEnumerable<TestCaseData> MalformedEncodings()
    {
        yield return new TestCaseData(Array.Empty<byte>(), false).SetName("ExactMissingTop");
        yield return new TestCaseData(new byte[] { 0x00 }, false).SetName("ExactPartialTop");
        yield return new TestCaseData(new byte[] { 0xaa, 0x00, 0x00 }, false).SetName("ExactUnexpectedPrefix");
        yield return new TestCaseData(new byte[] { 0x01, 0x00 }, false).SetName("ExactMissingSubword");
        yield return new TestCaseData(new byte[] { 0x00, 0x00, 0x01, 0x00 }, false).SetName("ExactEmptyOccupiedSubword");
        yield return new TestCaseData(Array.Empty<byte>(), true).SetName("BackwardMissingTop");
        yield return new TestCaseData(new byte[] { 0x00 }, true).SetName("BackwardPartialTop");
        yield return new TestCaseData(new byte[] { 0x01, 0x00 }, true).SetName("BackwardMissingSubword");
        yield return new TestCaseData(new byte[] { 0x00, 0x00, 0x01, 0x00 }, true).SetName("BackwardEmptyOccupiedSubword");
    }

    private static TestCaseData Case(string name, int[] setBits, byte[] encoded) =>
        new TestCaseData(setBits, encoded).SetName(name);

    private static bool TryRead(byte[] encoded) => CompactBitmap256.TryRead(encoded, out _);

    private static void ReadMalformed(byte[] encoded, bool backward)
    {
        if (backward)
        {
            CompactBitmap256.ReadFromEnd(encoded, out _);
        }
        else
        {
            CompactBitmap256.Read(encoded);
        }
    }

    private static byte[] Flat(int[] setBits)
    {
        byte[] flat = new byte[CompactBitmap256.FlatLength];
        foreach (int bit in setBits) flat[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
        return flat;
    }

    private static ulong[] Words(int[] setBits)
    {
        ulong[] words = new ulong[CompactBitmap256.WordCount];
        foreach (int bit in setBits) words[bit >> 6] |= 1ul << (63 - (bit & 63));
        return words;
    }

    private static int[] DenseBits()
    {
        int[] bits = new int[128];
        for (int i = 0; i < bits.Length; i++) bits[i] = i * 2;
        return bits;
    }

    private static int[] FullRange()
    {
        int[] bits = new int[CompactBitmap256.BitCount];
        for (int i = 0; i < bits.Length; i++) bits[i] = i;
        return bits;
    }

    private static byte[] DenseSubwords()
    {
        byte[] subwords = new byte[CompactBitmap256.GroupCount * CompactBitmap256.SubwordLength];
        Array.Fill(subwords, (byte)0xaa);
        return subwords;
    }

    private static byte[] FullSubwords()
    {
        byte[] subwords = new byte[CompactBitmap256.GroupCount * CompactBitmap256.SubwordLength];
        Array.Fill(subwords, byte.MaxValue);
        return subwords;
    }
}
