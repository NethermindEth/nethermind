// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Shared wire-format constants and footer helper for the two-level sorted table that backs a
/// persisted snapshot's metadata blob — an ascending byte-sorted map of fully-materialized keys to
/// small inline values, laid out as LevelDB-style size-bounded data blocks plus a separator-key
/// index at the tail.
/// </summary>
/// <remarks>
/// Layout within a table's <see cref="Bound"/> (offsets relative to the bound start):
/// <code>
///   data block × M: [numRestarts u16][restartOffset u16 × numRestarts][records...]
///                   records: [cp u8][suffixLen u8][keySuffix][vs u8][value]
///   separators:     [sepLen u8][sep bytes]   × M
///   sep offsets:    [sepEntryOffset u32]      × M       (first-level binary search operates on this)
///   block offsets:  [blockDataOffset u32]     × (M + 1) (last entry = separators-region start)
///   footer:         [count i64][numBlocks u32][restartInterval u8][version u8]   (fixed <see cref="FooterSize"/>)
/// </code>
/// Records are physically sorted and packed back-to-back into <see cref="BlockSizeTarget"/>-bounded
/// data blocks; within a block keys are front-coded against the previous record, resetting (<c>cp = 0</c>,
/// full key) every <see cref="RestartInterval"/> records and at every block start — these reset points
/// are the <em>restarts</em>. Each block prefixes a table of its restart byte offsets (relative to the
/// block start, a <c>u16</c> since a block stays well under 64 KiB) so a lookup can binary search the
/// restarts before scanning one restart run. The tail index stores, per block, the shortest
/// <em>separator</em> key in <c>[lastKey(block), firstKey(next block))</c> (the last block's separator is
/// its own last key); the first-level binary search is a lower bound over those separators (see
/// <see cref="SortedTableReader"/>). The fixed-width offset arrays sit last so the footer locates them
/// from <c>numBlocks</c> alone; <c>cp</c>, <c>suffixLen</c> and the value size <c>vs</c> are each one byte
/// (keys are ≤ 55 bytes; over-long values fail the builder's checked cast). Keys carry the column /
/// subcolumn tag bytes as <c>255 − tag</c> so a plain ascending sort reproduces the reverse-tag emission
/// order the HSST builder/compacter expect (see <see cref="PersistedSnapshotKey"/>).
/// </remarks>
internal static class SortedTable
{
    /// <summary>Target maximum on-disk size of a data block — a block closes once the next record
    /// would push it past this. Kept well under 64 KiB so in-block restart offsets fit a <c>u16</c>.</summary>
    internal const int BlockSizeTarget = 4096;

    /// <summary>Records per restart run — front-coding resets (<c>cp = 0</c>, full key) every this many
    /// records, and always at a block start, so each restart run decodes standalone.</summary>
    internal const int RestartInterval = 16;

    /// <summary>Width of an in-block restart offset (relative to the block start), a <c>u16</c>.</summary>
    internal const int RestartOffsetSize = sizeof(ushort);

    /// <summary>Width of a tail-index offset entry (separator offset, block data offset), a <c>u32</c>.</summary>
    internal const int IndexOffsetSize = sizeof(uint);

    /// <summary>Width of the single-byte record fields (common-prefix, key-suffix size, value size).</summary>
    internal const int SizePrefix = sizeof(byte);

    /// <summary>Fixed footer: record count (i64), block count (u32), restart interval (u8), version (u8).</summary>
    internal const int FooterSize = sizeof(long) + sizeof(uint) + 1 + 1;

    internal const byte FormatVersion = 4;

    /// <summary>Footer-resolved table geometry. Offsets are reader-absolute (<c>table.Offset</c> + relative).</summary>
    internal readonly record struct Footer(long Count, int NumBlocks, long SepOffsetsStart, long BlockOffsetsStart);

    /// <summary>
    /// Read the footer of the table occupying <paramref name="table"/> and resolve the record count,
    /// the block count, and the reader-absolute starts of the separator-offset and block-offset arrays.
    /// </summary>
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
        if (count < 0) return false;

        // Tail index, fixed-width-last: … [separators][sepOffsets u32 × M][blockOffsets u32 × (M+1)][footer].
        long blockOffsetsLength = (numBlocks + 1) * IndexOffsetSize;
        long sepOffsetsLength = numBlocks * IndexOffsetSize;
        if (blockOffsetsLength + sepOffsetsLength + FooterSize > table.Length) return false;

        long tableEnd = table.Offset + table.Length;
        long blockOffsetsStart = tableEnd - FooterSize - blockOffsetsLength;
        footer = new Footer(count, (int)numBlocks, blockOffsetsStart - sepOffsetsLength, blockOffsetsStart);
        return true;
    }
}
