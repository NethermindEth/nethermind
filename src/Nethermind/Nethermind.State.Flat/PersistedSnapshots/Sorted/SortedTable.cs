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
///   records:  [ks u16][key][vs u16][value] × N        (records in arbitrary insertion order)
///   offsets:  [recordOffset u32] × N                  (one per record, in ascending key order)
///   footer:   [count i64][version u8]                 (fixed <see cref="FooterSize"/> bytes, read first)
/// </code>
/// The offset region is the only sorted structure: <c>offsets[i]</c> is the byte offset (relative
/// to the table start) of the i-th record in ascending key order, so a binary search reads
/// <c>offsets[mid]</c>, seeks the record, and compares its inline key. Values are addressed by the
/// returned <see cref="Bound"/> and read separately. Keys carry the column / subcolumn tag bytes
/// as <c>255 − tag</c> so a plain ascending sort reproduces the reverse-tag emission order the
/// future HSST builder/compacter expect (see <see cref="PersistedSnapshotTags"/>).
/// </remarks>
internal static class SortedTable
{
    /// <summary>Width of each entry in the offset region — a u32 record offset (snapshots are capped at 2 GiB).</summary>
    internal const int OffsetSize = sizeof(uint);

    /// <summary>Width of the inline key-size and value-size prefixes on each record (u16 each).</summary>
    internal const int SizePrefix = sizeof(ushort);

    /// <summary>Fixed footer: record count (i64) followed by a format-version byte.</summary>
    internal const int FooterSize = sizeof(long) + 1;

    internal const byte FormatVersion = 1;

    /// <summary>
    /// Read the footer of the table occupying <paramref name="table"/> and resolve the record
    /// count and the absolute (reader-relative) start of the offset region.
    /// </summary>
    /// <returns><c>false</c> when the bound is too small, unreadable, or carries an unknown version.</returns>
    internal static bool TryReadFooter<TReader, TPin>(scoped in TReader reader, Bound table, out long count, out long offsetRegionStart)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        count = 0;
        offsetRegionStart = 0;
        if (table.Length < FooterSize) return false;

        Span<byte> footer = stackalloc byte[FooterSize];
        if (!reader.TryRead(table.Offset + table.Length - FooterSize, footer)) return false;
        if (footer[sizeof(long)] != FormatVersion) return false;

        long n = BinaryPrimitives.ReadInt64LittleEndian(footer);
        if (n < 0) return false;

        long offsetRegionLength = n * OffsetSize;
        if (offsetRegionLength + FooterSize > table.Length) return false;

        count = n;
        offsetRegionStart = table.Offset + table.Length - FooterSize - offsetRegionLength;
        return true;
    }
}
