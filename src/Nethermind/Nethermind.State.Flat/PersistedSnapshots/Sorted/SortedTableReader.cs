// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Lookup over a two-level <see cref="SortedTable"/>: a lower-bound binary search of the tail
/// separator index selects the block that can contain the key, then a binary search of that block's
/// restart table narrows to a restart run, which is scanned sequentially. O(log M) + O(log restarts)
/// random reads plus a short in-page scan. Wire layout: <see cref="SortedTable"/>.
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
        if (!SortedTable.TryReadFooter<TReader, TPin>(in reader, table, out SortedTable.Footer footer)
            || footer.NumBlocks == 0)
            return false;

        Span<byte> offBuf = stackalloc byte[SortedTable.IndexOffsetSize];
        Span<byte> hdr = stackalloc byte[2]; // [commonPrefix u8][suffixLen u8]

        // Stage 1: lower bound over separators — the first block whose separator >= target. A separator
        // can be a synthetic key in no block, so the in-block scan (stage 3) re-validates.
        int lo = 0;
        int hi = footer.NumBlocks; // exclusive
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (!reader.TryRead(footer.SepOffsetsStart + (long)mid * SortedTable.IndexOffsetSize, offBuf)) return false;
            long sepEntry = table.Offset + BinaryPrimitives.ReadUInt32LittleEndian(offBuf);
            if (!reader.TryRead(sepEntry, hdr[..1])) return false;
            int sepLen = hdr[0];
            using TPin sepPin = reader.PinBuffer(new Bound(sepEntry + SortedTable.SizePrefix, sepLen));
            if (sepPin.Buffer.SequenceCompareTo(key) >= 0) hi = mid; else lo = mid + 1;
        }
        if (lo == footer.NumBlocks) return false; // target exceeds the last separator (= last key) — miss
        int blockIdx = lo;

        // Resolve the block's data range [blockStart, blockEnd).
        if (!reader.TryRead(footer.BlockOffsetsStart + (long)blockIdx * SortedTable.IndexOffsetSize, offBuf)) return false;
        long blockStart = table.Offset + BinaryPrimitives.ReadUInt32LittleEndian(offBuf);
        if (!reader.TryRead(footer.BlockOffsetsStart + (long)(blockIdx + 1) * SortedTable.IndexOffsetSize, offBuf)) return false;
        long blockEnd = table.Offset + BinaryPrimitives.ReadUInt32LittleEndian(offBuf);

        Span<byte> u16 = stackalloc byte[SortedTable.RestartOffsetSize];
        if (!reader.TryRead(blockStart, u16)) return false;
        int numRestarts = BinaryPrimitives.ReadUInt16LittleEndian(u16);
        if (numRestarts == 0) return false;
        long restartTableStart = blockStart + SortedTable.RestartOffsetSize;

        // Stage 2: rightmost restart whose first key <= target. Restart-start records have cp == 0, so
        // the stored suffix is the full key.
        int rlo = 0;
        int rhi = numRestarts - 1;
        int found = -1;
        while (rlo <= rhi)
        {
            int rmid = rlo + ((rhi - rlo) >> 1);
            if (!reader.TryRead(restartTableStart + (long)rmid * SortedTable.RestartOffsetSize, u16)) return false;
            long recStart = blockStart + BinaryPrimitives.ReadUInt16LittleEndian(u16);
            if (!reader.TryRead(recStart, hdr)) return false;
            int firstKeyLen = hdr[1]; // hdr[0] (cp) == 0 at a restart start
            using TPin keyPin = reader.PinBuffer(new Bound(recStart + 2, firstKeyLen));
            if (keyPin.Buffer.SequenceCompareTo(key) <= 0) { found = rmid; rlo = rmid + 1; }
            else rhi = rmid - 1;
        }
        if (found < 0) return false; // target precedes the block's first key (gap) — miss

        // Stage 3: sequential scan of the found restart run, reconstructing front-coded keys.
        if (!reader.TryRead(restartTableStart + (long)found * SortedTable.RestartOffsetSize, u16)) return false;
        long pos = blockStart + BinaryPrimitives.ReadUInt16LittleEndian(u16);
        long runEnd;
        if (found + 1 < numRestarts)
        {
            if (!reader.TryRead(restartTableStart + (long)(found + 1) * SortedTable.RestartOffsetSize, u16)) return false;
            runEnd = blockStart + BinaryPrimitives.ReadUInt16LittleEndian(u16);
        }
        else
        {
            runEnd = blockEnd;
        }

        Span<byte> runningKey = stackalloc byte[256];
        while (pos < runEnd)
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
