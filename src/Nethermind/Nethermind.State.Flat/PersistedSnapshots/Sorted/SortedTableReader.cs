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
        Span<byte> hdr = stackalloc byte[2]; // [commonPrefix u8][suffixLen u8]

        // Stage 1: rightmost block whose first key <= target. Block-start records have cp == 0, so
        // the stored suffix is the full key.
        long lo = 0;
        long hi = blockCount - 1;
        long found = -1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (!reader.TryRead(offsetRegionStart + mid * SortedTable.OffsetSize, offsetBuf)) return false;
            long recordStart = table.Offset + BinaryPrimitives.ReadUInt32LittleEndian(offsetBuf);
            if (!reader.TryRead(recordStart, hdr)) return false;
            int firstKeyLen = hdr[1]; // hdr[0] (cp) == 0 at a block start
            using TPin keyPin = reader.PinBuffer(new Bound(recordStart + 2, firstKeyLen));
            if (keyPin.Buffer.SequenceCompareTo(key) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return false;

        // Stage 2: sequential scan of the found block, reconstructing front-coded keys.
        if (!reader.TryRead(offsetRegionStart + found * SortedTable.OffsetSize, offsetBuf)) return false;
        long pos = table.Offset + BinaryPrimitives.ReadUInt32LittleEndian(offsetBuf);
        long scanCount = Math.Min(blockSize, count - found * blockSize);
        Span<byte> runningKey = stackalloc byte[256];
        for (long j = 0; j < scanCount; j++)
        {
            if (!reader.TryRead(pos, hdr)) return false;
            int cp = hdr[0];
            int suffixLen = hdr[1];
            if (!reader.TryRead(pos + 2, runningKey.Slice(cp, suffixLen))) return false; // keep [0..cp) from prev
            int keyLen = cp + suffixLen;

            long valueSizeOffset = pos + 2 + suffixLen;
            if (!reader.TryRead(valueSizeOffset, hdr[..1])) return false;
            int valueLen = hdr[0];

            int cmp = key.SequenceCompareTo(runningKey[..keyLen]);
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
