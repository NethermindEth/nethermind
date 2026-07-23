// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;

namespace Nethermind.Pbt;

/// <summary>Adds the leaf-format byte to the compact bitmap trailing a non-empty <see cref="StemLeafBlob"/>.</summary>
internal readonly ref struct TwoLevelBitmapReader
{
    internal const int GroupCount = CompactBitmap256.GroupCount;
    internal const int BitmapLength = CompactBitmap256.FlatLength;
    internal const int FormatLength = sizeof(byte);

    private readonly CompactBitmap256 _presence;

    private TwoLevelBitmapReader(CompactBitmap256 presence) => _presence = presence;

    /// <summary>Which layout the non-empty <paramref name="blob"/> holds its entries in.</summary>
    /// <remarks>
    /// The footer itself is identical across the layouts; only the entries region differs.
    /// <see cref="FromBlob"/> is what validates the byte, so this is a plain read of a blob it has parsed.
    /// </remarks>
    public static PbtLeafFormat FormatOf(ReadOnlySpan<byte> blob) => (PbtLeafFormat)blob[^1];

    /// <summary>Parses the footer from the tail of a non-empty blob and yields the leading entries region.</summary>
    /// <exception cref="InvalidDataException">The trailing format byte is no <see cref="PbtLeafFormat"/>.</exception>
    public static TwoLevelBitmapReader FromBlob(ReadOnlySpan<byte> blob, out ReadOnlySpan<byte> entries)
    {
        if (blob.Length < CompactBitmap256.TopLength + FormatLength)
            throw new InvalidDataException("StemLeafBlob: compact bitmap footer is truncated");

        if (FormatOf(blob) is not (PbtLeafFormat.Legacy or PbtLeafFormat.EveryLevel or PbtLeafFormat.Interleaved or PbtLeafFormat.LeavesOnly or PbtLeafFormat.Every4Depth))
            throw new InvalidDataException($"StemLeafBlob: unexpected format byte 0x{blob[^1]:x2}");

        CompactBitmap256 presence = CompactBitmap256.ReadFromEnd(blob[..^FormatLength], out entries);
        if (entries.Length % StemLeafBlob.ValueLength != 0)
            throw new InvalidDataException("StemLeafBlob: entries region is truncated");

        return new TwoLevelBitmapReader(presence);
    }

    public int OccupiedGroups => _presence.OccupiedGroups;

    /// <summary>
    /// Whether leaf <paramref name="subIndex"/> is present, read straight from the two-level bitmap
    /// without materializing the flat form. Matches the MSB-first convention of the flat bitmap.
    /// </summary>
    public bool IsPresent(byte subIndex) => _presence.IsSet(subIndex);

    /// <summary>
    /// Materializes the flat 32-byte, MSB-first leaf bitmap that the tree-structural helpers
    /// (<c>RangeHasLeaf</c>/<c>CountLiveNodes</c>) operate on. Empty groups are cleared to zero.
    /// </summary>
    public void ExpandTo(Span<byte> flat) => _presence.ExpandTo(flat);

    /// <summary>
    /// Encodes a flat 32-byte MSB-first leaf bitmap as the footer (second-level, first-level, the
    /// <paramref name="format"/> byte) into <paramref name="footer"/>, returning the bytes written
    /// (<c>2·G + 3</c>).
    /// </summary>
    public static int Encode(ReadOnlySpan<byte> flat, Span<byte> footer, PbtLeafFormat format)
    {
        int bitmapLength = CompactBitmap256.Write(flat, footer);
        footer[bitmapLength] = (byte)format;
        return bitmapLength + FormatLength;
    }

    /// <summary>The exact footer length, including the format byte.</summary>
    public static int EncodedLength(ReadOnlySpan<byte> flat) => CompactBitmap256.EncodedLength(flat) + FormatLength;
}
