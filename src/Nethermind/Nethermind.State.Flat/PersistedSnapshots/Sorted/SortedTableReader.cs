// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Binary-search lookup over a single-level <see cref="SortedTable"/>. Each probe reads one
/// offset entry, seeks the record, and compares its inline key — O(log N) reader accesses, no
/// caching. Wire layout: <see cref="SortedTable"/>.
/// </summary>
internal static class SortedTableReader
{
    /// <summary>
    /// Seek <paramref name="key"/> in the table occupying <paramref name="table"/>. On a hit
    /// returns the reader-absolute <see cref="Bound"/> of the matching record's value (which the
    /// caller materializes via the reader).
    /// </summary>
    internal static bool TrySeek<TReader, TPin>(scoped in TReader reader, Bound table, scoped ReadOnlySpan<byte> key, out Bound value)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        value = default;
        if (!SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out long count, out long offsetRegionStart))
            return false;

        Span<byte> offsetBuf = stackalloc byte[SortedTable.OffsetSize];
        Span<byte> sizeBuf = stackalloc byte[SortedTable.SizePrefix];

        long lo = 0;
        long hi = count;
        while (lo < hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (!reader.TryRead(offsetRegionStart + mid * SortedTable.OffsetSize, offsetBuf)) return false;
            long recordStart = table.Offset + BinaryPrimitives.ReadUInt32LittleEndian(offsetBuf);

            if (!reader.TryRead(recordStart, sizeBuf)) return false;
            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(sizeBuf);

            using TPin keyPin = reader.PinBuffer(new Bound(recordStart + SortedTable.SizePrefix, keyLength));
            int cmp = key.SequenceCompareTo(keyPin.Buffer);
            if (cmp == 0)
            {
                long valueSizeOffset = recordStart + SortedTable.SizePrefix + keyLength;
                if (!reader.TryRead(valueSizeOffset, sizeBuf)) return false;
                int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(sizeBuf);
                value = new Bound(valueSizeOffset + SortedTable.SizePrefix, valueLength);
                return true;
            }
            if (cmp < 0) hi = mid; else lo = mid + 1;
        }
        return false;
    }
}
