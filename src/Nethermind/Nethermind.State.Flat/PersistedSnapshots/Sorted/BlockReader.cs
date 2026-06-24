// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>Read-side search and header parsing for a <see cref="Block"/>'s data records, whose values
/// are plain inline bytes. The index block's delta-coded values are read separately by
/// <see cref="IndexBlockReader"/>.</summary>
internal static class BlockReader
{
    /// <summary>Parse the block header at <paramref name="blockStart"/>: offset width, the
    /// block-relative records-end, restart count, and the block-relative records start.</summary>
    internal static bool ReadHeader<TReader, TPin>(scoped in TReader reader, long blockStart,
        out int width, out long recordsEnd, out long numRestarts, out long recordsStart)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        width = 0;
        recordsEnd = 0;
        numRestarts = 0;
        recordsStart = 0;

        // The body width (recordsEnd, numRestarts) is per-block variable — 2 bytes for a data Block,
        // 4 for the Index — so it cannot map to one fixed-layout struct; read the flag, then both
        // body fields together in a single read.
        Span<byte> buf = stackalloc byte[8]; // 2 × the max offset width (4, used by the Index)
        if (!reader.TryRead(blockStart, buf[..1])) return false;
        int w = Block.WidthFromFlag(buf[0]);
        if (w == 0) return false;
        if (!reader.TryRead(blockStart + 1, buf[..(2 * w)])) return false;
        recordsEnd = Block.ReadOffset(buf, w);
        numRestarts = Block.ReadOffset(buf[w..], w);
        width = w;
        recordsStart = Block.RecordsStart(w, numRestarts);
        return true;
    }

    /// <summary>
    /// Position at the first record whose key ≥ <paramref name="target"/> (the ceiling) in the data block
    /// at <paramref name="blockStart"/>: restart binary search then a forward scan to <c>recordsEnd</c>.
    /// On a hit copies the ceiling key into <paramref name="keyBuf"/> and returns its value
    /// <see cref="Bound"/>. Returns <c>false</c> when the block is empty or every key is
    /// &lt; <paramref name="target"/>.
    /// </summary>
    internal static bool SeekCeiling<TReader, TPin>(scoped in TReader reader, long blockStart,
        scoped ReadOnlySpan<byte> target, scoped Span<byte> keyBuf, out int keyLen, out Bound value)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        keyLen = 0;
        value = default;
        if (!ReadHeader<TReader, TPin>(in reader, blockStart, out int width, out long recordsEnd, out long numRestarts, out _))
            return false;
        if (numRestarts == 0) return false;

        long restartTableStart = blockStart + 1 + 2L * width;
        Span<byte> ob = stackalloc byte[4];
        Span<byte> hdr = stackalloc byte[2];

        // Rightmost restart whose first key <= target (cp == 0 there, so the suffix is the full key).
        long lo = 0;
        long hi = numRestarts - 1;
        long found = -1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (!reader.TryRead(restartTableStart + mid * width, ob[..width])) return false;
            long recStart = blockStart + Block.ReadOffset(ob, width);
            if (!reader.TryRead(recStart, hdr)) return false;
            int firstKeyLen = hdr[1];
            using TPin keyPin = reader.PinBuffer(new Bound(recStart + 2, firstKeyLen));
            if (keyPin.Buffer.SequenceCompareTo(target) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // target < firstKey ⇒ ceiling is the very first record; clamp the scan start to restart 0.
        long scanRestart = found < 0 ? 0 : found;
        if (!reader.TryRead(restartTableStart + scanRestart * width, ob[..width])) return false;
        long pos = blockStart + Block.ReadOffset(ob, width);
        long end = blockStart + recordsEnd;

        // Scan forward across restart boundaries (cp = 0 self-corrects) for the first key >= target.
        while (pos < end)
        {
            if (!reader.TryRead(pos, hdr)) return false;
            int cp = hdr[0];
            int suffixLen = hdr[1];
            if (!reader.TryRead(pos + 2, keyBuf.Slice(cp, suffixLen))) return false; // keep [0..cp) from prev
            int kLen = cp + suffixLen;

            long valueSizeOffset = pos + 2 + suffixLen;
            if (!reader.TryRead(valueSizeOffset, hdr[..1])) return false;
            int valueLen = hdr[0];

            if (target.SequenceCompareTo(keyBuf[..kLen]) <= 0)
            {
                keyLen = kLen;
                value = new Bound(valueSizeOffset + Block.SizePrefix, valueLen);
                return true;
            }
            pos = valueSizeOffset + Block.SizePrefix + valueLen;
        }
        return false;
    }
}
