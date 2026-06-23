// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Lookup over a single-level <see cref="SortedTable"/>: binary search the sparse offset region for
/// the block whose first key ≤ the target, then sequentially scan that block's ≤
/// <see cref="SortedTable.BlockSize"/> contiguous records. O(log(N/blockSize)) random reads plus a
/// short in-page scan. Wire layout: <see cref="SortedTable"/>.
/// </summary>
internal static class SortedTableReader
{
    /// <summary>
    /// Seek <paramref name="key"/> in the table occupying <paramref name="table"/>. On a hit returns
    /// the reader-absolute <see cref="Bound"/> of the matching record's value.
    /// </summary>
    internal static bool TrySeek<TReader, TPin>(scoped in TReader reader, Bound table, scoped ReadOnlySpan<byte> key, out Bound value)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        value = default;
        if (!SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out long count, out int blockSize, out long offsetRegionStart)
            || count == 0)
            return false;

        long blockCount = (count + blockSize - 1) / blockSize;
        Span<byte> offsetBuf = stackalloc byte[SortedTable.OffsetSize];
        Span<byte> sizeBuf = stackalloc byte[SortedTable.SizePrefix];

        // Stage 1: rightmost block whose first key <= target.
        long lo = 0;
        long hi = blockCount - 1;
        long found = -1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (!reader.TryRead(offsetRegionStart + mid * SortedTable.OffsetSize, offsetBuf)) return false;
            long recordStart = table.Offset + BinaryPrimitives.ReadUInt32LittleEndian(offsetBuf);
            if (!reader.TryRead(recordStart, sizeBuf)) return false;
            int firstKeyLen = sizeBuf[0];
            using TPin keyPin = reader.PinBuffer(new Bound(recordStart + SortedTable.SizePrefix, firstKeyLen));
            if (keyPin.Buffer.SequenceCompareTo(key) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return false;

        // Stage 2: sequential scan of the found block's records (contiguous, ascending).
        if (!reader.TryRead(offsetRegionStart + found * SortedTable.OffsetSize, offsetBuf)) return false;
        long pos = table.Offset + BinaryPrimitives.ReadUInt32LittleEndian(offsetBuf);
        long scanCount = Math.Min(blockSize, count - found * blockSize);
        for (long j = 0; j < scanCount; j++)
        {
            if (!reader.TryRead(pos, sizeBuf)) return false;
            int keyLen = sizeBuf[0];
            long keyOffset = pos + SortedTable.SizePrefix;
            long valueSizeOffset = keyOffset + keyLen;
            if (!reader.TryRead(valueSizeOffset, sizeBuf)) return false;
            int valueLen = sizeBuf[0];

            using TPin keyPin = reader.PinBuffer(new Bound(keyOffset, keyLen));
            int cmp = key.SequenceCompareTo(keyPin.Buffer);
            if (cmp == 0)
            {
                value = new Bound(valueSizeOffset + SortedTable.SizePrefix, valueLen);
                return true;
            }
            if (cmp < 0) return false; // records are ascending — target would have appeared by now
            pos = valueSizeOffset + SortedTable.SizePrefix + valueLen;
        }
        return false;
    }
}
