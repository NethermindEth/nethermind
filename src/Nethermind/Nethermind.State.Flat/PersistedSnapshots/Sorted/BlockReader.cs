// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>Read-side search and header parsing for a <see cref="Block"/>'s data records, whose values
/// are plain inline bytes. The index block's delta-coded values are read by
/// <see cref="IndexBlockReader"/>, which reuses <see cref="ReadHeader"/> and
/// <see cref="TryFindScanStart"/>.</summary>
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
    /// Locate where a ceiling scan for <paramref name="target"/> begins in the block at
    /// <paramref name="blockStart"/>: parse the header, then binary-search the restart table for the
    /// rightmost restart whose first key ≤ <paramref name="target"/> (clamped to restart 0 when the
    /// target precedes every key). Outputs that record's start <paramref name="pos"/>, the scan end
    /// <paramref name="end"/> (block-absolute <c>recordsEnd</c>), and the <paramref name="scanRestart"/>
    /// index. Returns <c>false</c> when the block has no restarts or is unreadable.
    /// </summary>
    /// <remarks>Shared by the data-block (<see cref="SeekCeiling"/>) and index-block
    /// (<see cref="IndexBlockReader.SeekCeiling"/>) seeks; the forward scan that follows differs only in
    /// how each interprets record values.</remarks>
    internal static bool TryFindScanStart<TReader, TPin>(scoped in TReader reader, long blockStart,
        scoped ReadOnlySpan<byte> target, out long pos, out long end, out long scanRestart)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        pos = 0;
        end = 0;
        scanRestart = 0;
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
        scanRestart = found < 0 ? 0 : found;
        if (!reader.TryRead(restartTableStart + scanRestart * width, ob[..width])) return false;
        pos = blockStart + Block.ReadOffset(ob, width);
        end = blockStart + recordsEnd;
        return true;
    }

    /// <summary>
    /// Position at the first record whose key ≥ <paramref name="target"/> (the ceiling) in the data block
    /// at <paramref name="blockStart"/>: restart binary search (<see cref="TryFindScanStart"/>) then a
    /// forward scan to <c>recordsEnd</c>. On a hit copies the ceiling key into <paramref name="keyBuf"/>
    /// and returns its value <see cref="Bound"/>. Returns <c>false</c> when the block is empty or every
    /// key is &lt; <paramref name="target"/>.
    /// </summary>
    internal static bool SeekCeiling<TReader, TPin>(scoped in TReader reader, long blockStart,
        scoped ReadOnlySpan<byte> target, scoped Span<byte> keyBuf, out int keyLen, out Bound value)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        keyLen = 0;
        value = default;
        if (!TryFindScanStart<TReader, TPin>(in reader, blockStart, target, out long pos, out long end, out _))
            return false;

        Span<byte> hdr = stackalloc byte[2];

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
