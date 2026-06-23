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
///   data block × M  ; blocks 0..M-2 zero-padded to BlockSize (4096); block i at i·BlockSize
///   index block     ; right after the last (unpadded) data block; key = separator, value = u32 block number LE
///   footer          ; [count i64][numBlocks u32][lastBlockSize u16][restartInterval u8][version u8]  (fixed FooterSize)
/// </code>
/// Each data block holds a slice of the sorted records; the index block maps the shortest separator in
/// <c>[lastKey(block i), firstKey(block i+1))</c> (the last block's separator is its own last key) to
/// the block number, so a lookup is two <see cref="BlockReader.SeekCeiling"/> calls (index → block
/// number → data block). Addressing blocks by number (× BlockSize) rather than byte offset lets a u32
/// reach a 16 TiB table. Only blocks 0..M-2 are padded — the last data block is not, so a small (single
/// block) table stays compact; the footer's <c>lastBlockSize</c> locates the index right after it. Both
/// data and index blocks are self-describing (see <see cref="Block"/>), so search needs only a block's
/// start. Keys carry the column / subcolumn tag bytes as <c>255 − tag</c> so a plain ascending sort
/// reproduces the reverse-tag emission order the HSST builder/compacter expect (see
/// <see cref="PersistedSnapshotKey"/>).
/// </remarks>
internal static class SortedTable
{
    /// <summary>Data-block size and alignment — every data block is zero-padded to this and addressed
    /// by block number (byte offset = blockNumber · BlockSize).</summary>
    internal const int BlockSize = PageLayout.PageSize;

    /// <summary>Default front-coding restart interval (records per restart run).</summary>
    internal const int DefaultRestartInterval = 8;

    /// <summary>Width of an index block's value — a u32 block number.</summary>
    internal const int IndexValueSize = sizeof(uint);

    /// <summary>Fixed footer: record count (i64), block count (u32), last-block size (u16),
    /// restart interval (u8), version (u8).</summary>
    internal const int FooterSize = sizeof(long) + sizeof(uint) + sizeof(ushort) + 1 + 1;

    internal const byte FormatVersion = 5;

    /// <summary>Footer-resolved table geometry: total record count, data-block count, and the byte size
    /// of the last (unpadded) data block.</summary>
    internal readonly record struct Footer(long Count, int NumBlocks, int LastBlockSize);

    /// <summary>Reader-absolute start of the index block (= just past the last, unpadded, data block).</summary>
    internal static long IndexBlockStart(Bound table, in Footer footer) =>
        footer.NumBlocks == 0 ? table.Offset : table.Offset + (long)(footer.NumBlocks - 1) * BlockSize + footer.LastBlockSize;

    /// <summary>Reader-absolute start of data block <paramref name="blockNumber"/>.</summary>
    internal static long DataBlockStart(Bound table, long blockNumber) => table.Offset + blockNumber * BlockSize;

    /// <summary>Read the footer of the table occupying <paramref name="table"/> and resolve the record
    /// count, data-block count, and last-block size.</summary>
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
        long numBlocks = BinaryPrimitives.ReadUInt32LittleEndian(buf[sizeof(long)..]);
        int lastBlockSize = BinaryPrimitives.ReadUInt16LittleEndian(buf[(sizeof(long) + sizeof(uint))..]);
        // Bound numBlocks by the actual table size before the int cast / offset math below, so a
        // corrupt footer cannot overflow to a negative count or address outside the bound.
        if (count < 0 || lastBlockSize > BlockSize || numBlocks > table.Length / BlockSize + 1) return false;

        footer = new Footer(count, (int)numBlocks, lastBlockSize);
        // The index block starts past the data region and the footer follows it.
        if (IndexBlockStart(table, footer) + FooterSize > table.Offset + table.Length) return false;
        return true;
    }
}
