// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>
/// Reads (and encodes) the two-level leaf-presence bitmap that trails a non-empty
/// <see cref="StemLeafBlob"/>: <c>[ second-level: 2·G ][ first-level top: 2 ][ format: 1 ]</c>.
/// </summary>
/// <remarks>
/// The 256 leaves are split into <see cref="GroupCount"/> groups of 16. The first-level word marks
/// which groups are occupied; each occupied group contributes one 2-byte sub-word — its 16 leaf bits
/// copied verbatim (MSB-first) from the flat bitmap — in ascending group order. This compacts the
/// clustered, mostly-sparse leaf occupancy from a flat 32-byte bitmap to <c>2·G + 2</c> bytes, and the
/// trailing format byte makes the blob self-describing. Tree-structural queries stay in
/// <see cref="StemLeafBlob"/> and run over the flat bitmap produced by <see cref="ExpandTo"/>.
/// </remarks>
internal readonly ref struct TwoLevelBitmapReader
{
    internal const int GroupCount = 16;      // 256 leaves / 16 leaves per group
    internal const int SubWordLength = 2;
    internal const int TopLength = 2;
    internal const int FormatLength = 1;
    internal const byte FormatByte = 0x01;   // version sentinel, validated on decode
    internal const int BitmapLength = 32;    // flat expansion length

    private readonly ReadOnlySpan<byte> _subwords;   // 2·G bytes, ascending occupied-group order
    private readonly ushort _top;

    private TwoLevelBitmapReader(ReadOnlySpan<byte> subwords, ushort top)
    {
        _subwords = subwords;
        _top = top;
    }

    /// <summary>Parses the footer from the tail of a non-empty blob and yields the leading entries region.</summary>
    /// <exception cref="InvalidDataException">The trailing format byte is not <see cref="FormatByte"/>.</exception>
    public static TwoLevelBitmapReader FromBlob(ReadOnlySpan<byte> blob, out ReadOnlySpan<byte> entries)
    {
        if (blob[^1] != FormatByte)
            throw new InvalidDataException($"StemLeafBlob: unexpected format byte 0x{blob[^1]:x2}");

        ushort top = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(blob.Length - TopLength - FormatLength, TopLength));
        int g = BitOperations.PopCount((uint)top);
        int footerLength = SubWordLength * g + TopLength + FormatLength;
        entries = blob[..(blob.Length - footerLength)];
        return new TwoLevelBitmapReader(blob.Slice(blob.Length - footerLength, SubWordLength * g), top);
    }

    /// <summary>The number of occupied groups (the second-level sub-word count).</summary>
    public int OccupiedGroups => BitOperations.PopCount((uint)_top);

    /// <summary>The sub-word slot of group <paramref name="g"/> (its rank among occupied groups).</summary>
    private int SubSlot(int g) => BitOperations.PopCount((uint)(_top & ((1 << g) - 1)));

    /// <summary>
    /// Whether leaf <paramref name="subIndex"/> is present, read straight from the two-level bitmap
    /// without materializing the flat form. Matches the MSB-first convention of the flat bitmap.
    /// </summary>
    public bool IsPresent(byte subIndex)
    {
        int g = subIndex >> 4;
        if ((_top & (1 << g)) == 0) return false;

        int byteInGroup = (subIndex & 0xF) >> 3;   // 0 or 1
        int bit = 7 - (subIndex & 7);              // MSB-first, matches StemLeafBlob.IsPresent(flat, …)
        return (_subwords[SubSlot(g) * SubWordLength + byteInGroup] & (1 << bit)) != 0;
    }

    /// <summary>
    /// Materializes the flat 32-byte, MSB-first leaf bitmap that the tree-structural helpers
    /// (<c>RangeHasLeaf</c>/<c>CountLiveNodes</c>) operate on. Empty groups are cleared to zero.
    /// </summary>
    public void ExpandTo(Span<byte> flat)
    {
        flat.Clear();   // empty groups must read back zero; do not rely on caller/implicit zeroing
        int slot = 0;
        for (int g = 0; g < GroupCount; g++)
        {
            if ((_top & (1 << g)) != 0)
            {
                _subwords.Slice(slot * SubWordLength, SubWordLength).CopyTo(flat[(2 * g)..]);
                slot++;
            }
        }
    }

    /// <summary>
    /// Encodes a flat 32-byte MSB-first leaf bitmap as the footer (second-level, first-level, format
    /// byte) into <paramref name="footer"/>, returning the bytes written (<c>2·G + 3</c>).
    /// </summary>
    public static int Encode(ReadOnlySpan<byte> flat, Span<byte> footer)
    {
        ushort top = 0;
        int slot = 0;
        for (int g = 0; g < GroupCount; g++)
        {
            if ((flat[2 * g] | flat[2 * g + 1]) != 0)   // == RangeHasLeaf(flat, 16g, 16g+16)
            {
                top |= (ushort)(1 << g);
                flat.Slice(2 * g, SubWordLength).CopyTo(footer[(slot * SubWordLength)..]);
                slot++;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(footer.Slice(slot * SubWordLength, TopLength), top);
        footer[slot * SubWordLength + TopLength] = FormatByte;
        return slot * SubWordLength + TopLength + FormatLength;
    }

    /// <summary>The number of occupied groups of a flat bitmap — a sizing helper used before renting.</summary>
    public static int OccupiedGroupsOf(ReadOnlySpan<byte> flat)
    {
        int count = 0;
        for (int g = 0; g < GroupCount; g++)
            if ((flat[2 * g] | flat[2 * g + 1]) != 0) count++;
        return count;
    }
}
