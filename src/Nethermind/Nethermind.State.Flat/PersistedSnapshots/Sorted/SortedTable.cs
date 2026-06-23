// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Shared wire-format constants and footer helpers for the deliberately-unoptimized,
/// single-level sorted table that backs a persisted snapshot's metadata blob. The table is a
/// plain ascending byte-sorted map of fully-materialized keys to small inline values; lookups
/// are binary search only (no nested indexes, no per-table bloom).
/// </summary>
/// <remarks>
/// Layout within a table's <see cref="Bound"/> (offsets relative to the bound start):
/// <code>
///   records (sorted, contiguous): [cp u8][suffixLen u8][keySuffix][vs u8][value] × N
///   sparse offsets:               [recordOffset u32] × ceil(N / BlockSize)
///   footer:                       [count i64][blockSize u8][version u8]   (fixed <see cref="FooterSize"/>)
/// </code>
/// Records are physically sorted and packed back-to-back, with keys front-coded: <c>cp</c> is the
/// number of leading bytes shared with the previous record's key and <c>keySuffix</c> the remaining
/// <c>suffixLen</c> bytes, so the full key = previous key's first <c>cp</c> bytes + <c>keySuffix</c>.
/// The first record of every block has <c>cp = 0</c> (full key) so a block decodes standalone. The
/// sparse offset region stores the byte offset (relative to the table start) of the first record of
/// every <see cref="BlockSize"/>-record block, in ascending key order. A lookup binary searches the
/// sparse offsets for the block whose first key ≤ the target, then sequentially scans that block's
/// ≤ <see cref="BlockSize"/> records (contiguous, almost always within one page), reconstructing
/// keys with a running buffer; see <see cref="SortedTableReader"/>. The common-prefix, key-suffix
/// and value sizes are each a single byte (keys are ≤ 55 bytes; over-long values fail the builder's
/// checked cast — the one variable-length datum, the blob-arena id list, is stored as per-id records
/// instead). Keys carry the column / subcolumn
/// tag bytes as <c>255 − tag</c> so a plain ascending sort reproduces the reverse-tag emission order
/// the future HSST builder/compacter expect (see <see cref="PersistedSnapshotKey"/>).
/// </remarks>
internal static class SortedTable
{
    /// <summary>Number of records per sparse-offset block — the binary search narrows to a block,
    /// then sequentially scans up to this many contiguous records.</summary>
    internal const int BlockSize = 8;

    /// <summary>Width of each entry in the offset region — a u32 record offset (snapshots ≤ 2 GiB).</summary>
    internal const int OffsetSize = sizeof(uint);

    /// <summary>Width of the single-byte record fields (common-prefix, key-suffix size, value size).</summary>
    internal const int SizePrefix = sizeof(byte);

    /// <summary>Fixed footer: record count (i64), block size (u8), format-version byte.</summary>
    internal const int FooterSize = sizeof(long) + 1 + 1;

    internal const byte FormatVersion = 3;

    /// <summary>
    /// Read the footer of the table occupying <paramref name="table"/> and resolve the record count,
    /// the on-disk block size, and the absolute (reader-relative) start of the sparse offset region.
    /// </summary>
    /// <returns><c>false</c> when the bound is too small, unreadable, or carries an unknown version.</returns>
    internal static bool TryReadFooter<TReader, TPin>(scoped in TReader reader, Bound table, out long count, out int blockSize, out long offsetRegionStart)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        count = 0;
        blockSize = 0;
        offsetRegionStart = 0;
        if (table.Length < FooterSize) return false;

        Span<byte> footer = stackalloc byte[FooterSize];
        if (!reader.TryRead(table.Offset + table.Length - FooterSize, footer)) return false;
        if (footer[sizeof(long) + 1] != FormatVersion) return false;

        long n = BinaryPrimitives.ReadInt64LittleEndian(footer);
        int bs = footer[sizeof(long)];
        if (n < 0 || bs <= 0) return false;

        long blockCount = (n + bs - 1) / bs;
        long offsetRegionLength = blockCount * OffsetSize;
        if (offsetRegionLength + FooterSize > table.Length) return false;

        count = n;
        blockSize = bs;
        offsetRegionStart = table.Offset + table.Length - FooterSize - offsetRegionLength;
        return true;
    }
}
