// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Shared wire-format constants and footer helper for the two-level sorted table that backs a
/// persisted snapshot's metadata blob. It is an ascending byte-sorted map of fully-materialized keys
/// to small inline values, laid out as a run of 4 KiB-aligned <see cref="Block">data blocks</see>
/// addressed by block number, followed by a single index block (separator → block number) and a footer.
/// </summary>
/// <remarks>
/// Layout within a table's <see cref="Bound"/> (offsets relative to the bound start):
/// <code>
///   data block × M  ; blocks 0..M-2 zero-padded to BlockSize (4096); block i at i·BlockSize.
///                     The last data block (M-1) is NOT padded — the index follows it immediately.
///   index block     ; one Block at byte offset <c>indexOffset</c>; NOT block-aligned (it is located by
///                     the footer, not addressed by block number); key = separator, value = u32 block number LE
///   footer          ; [count i64][numDataBlocks i64][indexOffset i64][restartInterval u8][version u8]  (fixed FooterSize)
/// </code>
/// Each data block holds a slice of the sorted records; the index block maps the shortest separator in
/// <c>[lastKey(block i), firstKey(block i+1))</c> (the last block's separator is its own last key) to
/// the block number, so a lookup is two <see cref="BlockReader.SeekCeiling"/> calls (index → block
/// number → data block). Data blocks are addressed by number (× BlockSize), so a u32 block number
/// reaches a 16 TiB data region; the single index block is addressed directly by the footer's
/// <c>indexOffset</c>, so it needs no padding and the footer fields are i64 to span the full range.
/// Both data and index blocks are self-describing (see <see cref="Block"/>), so search needs only a
/// block's start. Keys carry the column / subcolumn tag bytes as <c>255 − tag</c> so a plain ascending
/// sort reproduces the reverse-tag emission order the HSST builder/compacter expect (see
/// <see cref="PersistedSnapshotKey"/>).
/// </remarks>
internal static class SortedTable
{
    /// <summary>Data-block size and alignment — every data block but the last is zero-padded to this and
    /// addressed by block number (byte offset = blockNumber · BlockSize).</summary>
    internal const int BlockSize = PageLayout.PageSize;

    /// <summary>Default front-coding restart interval (records per restart run).</summary>
    internal const int DefaultRestartInterval = 8;

    /// <summary>Width of an index block's value — a u32 block number.</summary>
    internal const int IndexValueSize = sizeof(uint);

    /// <summary>Fixed footer: record count (i64), data-block count (i64), index-block byte offset (i64),
    /// restart interval (u8), version (u8).</summary>
    internal const int FooterSize = sizeof(long) + sizeof(long) + sizeof(long) + 1 + 1;

    internal const byte FormatVersion = 6;

    /// <summary>Footer-resolved table geometry: total record count, data-block count, and the
    /// table-relative byte offset of the (unaligned) index block.</summary>
    internal readonly record struct Footer(long Count, long NumDataBlocks, long IndexOffset);

    /// <summary>Reader-absolute start of the index block.</summary>
    internal static long IndexBlockStart(Bound table, in Footer footer) => table.Offset + footer.IndexOffset;

    /// <summary>Reader-absolute start of data block <paramref name="blockNumber"/>.</summary>
    internal static long DataBlockStart(Bound table, long blockNumber) => table.Offset + blockNumber * BlockSize;

    /// <summary>Read the footer of the table occupying <paramref name="table"/> and resolve the record
    /// count, data-block count, and index-block offset.</summary>
    /// <returns><c>false</c> when the bound is too small, unreadable, or carries an unknown version.</returns>
    internal static bool TryReadFooter<TReader, TPin>(scoped in TReader reader, Bound table, out Footer footer)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        footer = default;
        if (table.Length < FooterSize) return false;

        Span<byte> buf = stackalloc byte[FooterSize];
        if (!reader.TryRead(table.Offset + table.Length - FooterSize, buf)) return false;
        if (buf[FooterSize - 1] != FormatVersion) return false;

        long count = BinaryPrimitives.ReadInt64LittleEndian(buf);
        long numDataBlocks = BinaryPrimitives.ReadInt64LittleEndian(buf[sizeof(long)..]);
        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(buf[(2 * sizeof(long))..]);
        // Bound the fields by the actual table size so a corrupt footer cannot address outside the
        // bound: data blocks live in [0, indexOffset) and the index block + footer fill the tail.
        if (count < 0 || numDataBlocks < 0 || indexOffset < 0) return false;
        if (numDataBlocks > table.Length / BlockSize + 1) return false;
        if (indexOffset > table.Length - FooterSize) return false;

        footer = new Footer(count, numDataBlocks, indexOffset);
        return true;
    }
}
