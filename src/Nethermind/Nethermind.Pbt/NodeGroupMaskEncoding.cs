// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Nethermind.Pbt;

internal static class NodeGroupMaskEncoding
{
    public static int EncodedLength<TLayout>(ReadOnlySpan<ulong> chains) where TLayout : IPbtTileLayout =>
        TLayout.HasCompactBoundaryMask
            ? 2 * TLayout.PositionMaskWordCount * sizeof(ulong) + CompactChainsLength(chains)
            : TLayout.MaskTrailerLength;

    public static int Write<TLayout>(Span<byte> destination, ReadOnlySpan<ulong> presence, ReadOnlySpan<ulong> stems, ReadOnlySpan<ulong> chains)
        where TLayout : IPbtTileLayout
    {
        if (!TLayout.HasCompactBoundaryMask)
        {
            UInt128 presenceValue = ReadUInt128(presence);
            UInt128 stemsValue = ReadUInt128(stems);
            TLayout.WriteMasks(destination, new NodeGroupBitmasks(presenceValue, stemsValue, chains[0]));
            return TLayout.MaskTrailerLength;
        }

        int positionBytes = TLayout.PositionMaskWordCount * sizeof(ulong);
        WriteWords(destination, presence[..TLayout.PositionMaskWordCount]);
        WriteWords(destination[positionBytes..], stems[..TLayout.PositionMaskWordCount]);
        int chainsLength = WriteCompactChains(chains[..TLayout.BoundaryMaskWordCount], destination[(2 * positionBytes)..]);
        return 2 * positionBytes + chainsLength;
    }

    public static void Read<TLayout>(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> entries, out ReadOnlySpan<byte> presence,
        out ReadOnlySpan<byte> stems, out ReadOnlySpan<byte> chains, out CompactBitmap256 compactChains, out int maskLength)
        where TLayout : IPbtTileLayout
    {
        compactChains = default;
        int fixedTailLength = PbtSubtreeStats.EncodedLength + sizeof(byte);
        ReadOnlySpan<byte> beforeFixedTail = data[..^fixedTailLength];
        if (!TLayout.HasCompactBoundaryMask)
        {
            maskLength = TLayout.MaskTrailerLength;
            if (beforeFixedTail.Length < maskLength) throw new InvalidDataException("Trie node group mask trailer is truncated");
            entries = beforeFixedTail[..^maskLength];
            ReadOnlySpan<byte> masks = beforeFixedTail[^maskLength..];
            int positionBytes = TLayout.PositionCount <= 31 ? sizeof(uint) : 2 * sizeof(ulong);
            presence = masks[..positionBytes];
            stems = masks.Slice(positionBytes, positionBytes);
            chains = masks[(2 * positionBytes)..];
            return;
        }

        compactChains = CompactBitmap256.ReadFromEnd(beforeFixedTail, out ReadOnlySpan<byte> prefix);
        int widePositionBytes = TLayout.PositionMaskWordCount * sizeof(ulong);
        int fixedMaskLength = 2 * widePositionBytes;
        if (prefix.Length < fixedMaskLength) throw new InvalidDataException("Trie node group position masks are truncated");
        entries = prefix[..^fixedMaskLength];
        presence = prefix.Slice(prefix.Length - fixedMaskLength, widePositionBytes);
        stems = prefix[^widePositionBytes..];
        chains = default;
        maskLength = fixedMaskLength + compactChains.Length;
    }

    public static bool Contains(ReadOnlySpan<byte> littleEndianWords, int bit) =>
        (littleEndianWords[bit >> 3] & (1 << (bit & 7))) != 0;

    public static int PopCount(ReadOnlySpan<byte> bytes)
    {
        int count = 0;
        foreach (byte value in bytes) count += BitOperations.PopCount(value);
        return count;
    }

    public static int PopCountBelow(ReadOnlySpan<byte> littleEndianWords, int bit)
    {
        int wholeBytes = bit >> 3;
        int count = PopCount(littleEndianWords[..wholeBytes]);
        if ((bit & 7) != 0) count += BitOperations.PopCount((uint)(littleEndianWords[wholeBytes] & ((1 << (bit & 7)) - 1)));
        return count;
    }

    private static UInt128 ReadUInt128(ReadOnlySpan<ulong> words) =>
        words.Length == 1 ? words[0] : new UInt128(words[1], words[0]);

    private static void WriteWords(Span<byte> destination, ReadOnlySpan<ulong> words)
    {
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(destination[(i * sizeof(ulong))..], words[i]);
    }

    private static int CompactChainsLength(ReadOnlySpan<ulong> words)
    {
        Span<byte> flat = stackalloc byte[CompactBitmap256.FlatLength];
        ExpandChains(words, flat);
        return CompactBitmap256.EncodedLength(flat);
    }

    private static int WriteCompactChains(ReadOnlySpan<ulong> words, Span<byte> destination)
    {
        Span<byte> flat = stackalloc byte[CompactBitmap256.FlatLength];
        ExpandChains(words, flat);
        return CompactBitmap256.Write(flat, destination);
    }

    private static void ExpandChains(ReadOnlySpan<ulong> words, Span<byte> flat)
    {
        flat.Clear();
        for (int bit = 0; bit < CompactBitmap256.BitCount; bit++)
            if (PbtBitset.Contains(words, bit)) flat[bit >> 3] |= (byte)(1 << (7 - (bit & 7)));
    }
}
