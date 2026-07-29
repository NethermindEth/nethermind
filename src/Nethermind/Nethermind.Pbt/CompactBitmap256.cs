// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>Reads and writes a compact 256-bit bitmap encoded as <c>[ occupied subwords ][ top ]</c>.</summary>
/// <remarks>
/// The bits are split into 16 groups of 16. The little-endian top word marks occupied groups, and each
/// occupied group contributes its 16-bit subword, in ascending group order and the same big-endian bit
/// order as the 32-byte flat form.
/// </remarks>
internal readonly ref struct CompactBitmap256
{
    internal const int BitCount = 256;
    internal const int FlatLength = BitCount / 8;
    internal const int WordCount = BitCount / 64;
    internal const int GroupCount = BitCount / 16;
    internal const int SubwordLength = sizeof(ushort);
    internal const int TopLength = sizeof(ushort);
    internal const int MaxEncodedLength = GroupCount * SubwordLength + TopLength;

    private readonly ReadOnlySpan<byte> _subwords;
    private readonly ushort _top;

    private CompactBitmap256(ReadOnlySpan<byte> subwords, ushort top)
    {
        _subwords = subwords;
        _top = top;
    }

    public int OccupiedGroups => BitOperations.PopCount((uint)_top);

    public int Length => _subwords.Length + TopLength;

    /// <summary>Reads an encoding that occupies all of <paramref name="encoded"/>.</summary>
    public static bool TryRead(ReadOnlySpan<byte> encoded, out CompactBitmap256 bitmap)
    {
        bitmap = default;
        if (encoded.Length < TopLength) return false;

        ushort top = BinaryPrimitives.ReadUInt16LittleEndian(encoded[^TopLength..]);
        int subwordsLength = BitOperations.PopCount((uint)top) * SubwordLength;
        if (encoded.Length != subwordsLength + TopLength || HasEmptySubword(encoded[..subwordsLength])) return false;

        bitmap = new CompactBitmap256(encoded[..subwordsLength], top);
        return true;
    }

    /// <summary>Reads an encoding that occupies all of <paramref name="encoded"/>.</summary>
    /// <exception cref="InvalidDataException"><paramref name="encoded"/> does not have the length its top word specifies.</exception>
    public static CompactBitmap256 Read(ReadOnlySpan<byte> encoded)
    {
        if (!TryRead(encoded, out CompactBitmap256 bitmap))
            throw new InvalidDataException("Compact 256-bit bitmap has an invalid encoded length");

        return bitmap;
    }

    /// <summary>Reads an encoding from the end of <paramref name="source"/> and returns the bytes before it.</summary>
    /// <exception cref="InvalidDataException"><paramref name="source"/> is too short for the encoding its top word specifies.</exception>
    public static CompactBitmap256 ReadFromEnd(ReadOnlySpan<byte> source, out ReadOnlySpan<byte> prefix)
    {
        if (source.Length < TopLength)
            throw new InvalidDataException("Compact 256-bit bitmap is truncated before its top word");

        ushort top = BinaryPrimitives.ReadUInt16LittleEndian(source[^TopLength..]);
        int length = BitOperations.PopCount((uint)top) * SubwordLength + TopLength;
        if (source.Length < length)
            throw new InvalidDataException("Compact 256-bit bitmap is truncated before its subwords");

        ReadOnlySpan<byte> subwords = source.Slice(source.Length - length, length - TopLength);
        if (HasEmptySubword(subwords))
            throw new InvalidDataException("Compact 256-bit bitmap contains an empty occupied subword");

        prefix = source[..^length];
        return new CompactBitmap256(subwords, top);
    }

    public bool IsSet(int bitIndex)
    {
        int group = bitIndex >> 4;
        if ((_top & (1 << group)) == 0) return false;

        int byteInGroup = (bitIndex & 0xF) >> 3;
        int bit = 7 - (bitIndex & 7);
        return (_subwords[SubwordSlot(group) * SubwordLength + byteInGroup] & (1 << bit)) != 0;
    }

    public void ExpandTo(Span<byte> flat)
    {
        if (flat.Length < FlatLength)
            throw new ArgumentException($"Destination must hold at least {FlatLength} bytes", nameof(flat));

        flat = flat[..FlatLength];
        flat.Clear();
        int slot = 0;
        for (int group = 0; group < GroupCount; group++)
        {
            if ((_top & (1 << group)) == 0) continue;

            _subwords.Slice(slot * SubwordLength, SubwordLength).CopyTo(flat[(group * SubwordLength)..]);
            slot++;
        }
    }

    public static int EncodedLength(ReadOnlySpan<byte> flat) =>
        CountOccupiedGroups(flat) * SubwordLength + TopLength;

    /// <remarks>
    /// The four words are in logical order; in each word bit 63 is the first bitmap bit and bit 0 the last.
    /// </remarks>
    public static int EncodedLength(ReadOnlySpan<ulong> flat) =>
        CountOccupiedGroups(flat) * SubwordLength + TopLength;

    public static int Write(ReadOnlySpan<byte> flat, Span<byte> destination)
    {
        RequireFlatLength(flat.Length, FlatLength, nameof(flat));
        int length = EncodedLength(flat);
        RequireDestinationLength(destination, length);

        ushort top = 0;
        int slot = 0;
        for (int group = 0; group < GroupCount; group++)
        {
            ReadOnlySpan<byte> subword = flat.Slice(group * SubwordLength, SubwordLength);
            if ((subword[0] | subword[1]) == 0) continue;

            top |= (ushort)(1 << group);
            subword.CopyTo(destination[(slot * SubwordLength)..]);
            slot++;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(slot * SubwordLength, TopLength), top);
        return length;
    }

    /// <remarks>
    /// The four words are in logical order; in each word bit 63 is the first bitmap bit and bit 0 the last.
    /// </remarks>
    public static int Write(ReadOnlySpan<ulong> flat, Span<byte> destination)
    {
        RequireFlatLength(flat.Length, WordCount, nameof(flat));
        int length = EncodedLength(flat);
        RequireDestinationLength(destination, length);

        ushort top = 0;
        int slot = 0;
        for (int group = 0; group < GroupCount; group++)
        {
            int groupInWord = group & 3;
            ushort subword = (ushort)(flat[group >> 2] >> (48 - groupInWord * 16));
            if (subword == 0) continue;

            top |= (ushort)(1 << group);
            BinaryPrimitives.WriteUInt16BigEndian(destination[(slot * SubwordLength)..], subword);
            slot++;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(slot * SubwordLength, TopLength), top);
        return length;
    }

    private int SubwordSlot(int group) => BitOperations.PopCount((uint)(_top & ((1 << group) - 1)));

    private static int CountOccupiedGroups(ReadOnlySpan<byte> flat)
    {
        RequireFlatLength(flat.Length, FlatLength, nameof(flat));
        int count = 0;
        for (int group = 0; group < GroupCount; group++)
        {
            int offset = group * SubwordLength;
            if ((flat[offset] | flat[offset + 1]) != 0) count++;
        }

        return count;
    }

    private static int CountOccupiedGroups(ReadOnlySpan<ulong> flat)
    {
        RequireFlatLength(flat.Length, WordCount, nameof(flat));
        int count = 0;
        for (int group = 0; group < GroupCount; group++)
        {
            int groupInWord = group & 3;
            if ((ushort)(flat[group >> 2] >> (48 - groupInWord * 16)) != 0) count++;
        }

        return count;
    }

    private static bool HasEmptySubword(ReadOnlySpan<byte> subwords)
    {
        for (int offset = 0; offset < subwords.Length; offset += SubwordLength)
            if ((subwords[offset] | subwords[offset + 1]) == 0) return true;

        return false;
    }

    private static void RequireFlatLength(int actual, int expected, string parameterName)
    {
        if (actual != expected)
            throw new ArgumentException($"Bitmap must contain exactly {expected} elements", parameterName);
    }

    private static void RequireDestinationLength(Span<byte> destination, int required)
    {
        if (destination.Length < required)
            throw new ArgumentException($"Destination must hold at least {required} bytes", nameof(destination));
    }
}
