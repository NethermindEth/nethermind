// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>Read-side search and header parsing for a <see cref="Block"/>.</summary>
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

        Span<byte> buf = stackalloc byte[4];
        if (!reader.TryRead(blockStart, buf[..1])) return false;
        int w = Block.WidthFromFlag(buf[0]);
        if (w == 0) return false;
        if (!reader.TryRead(blockStart + 1, buf[..w])) return false;
        recordsEnd = Block.ReadOffset(buf, w);
        if (!reader.TryRead(blockStart + 1 + w, buf[..w])) return false;
        numRestarts = Block.ReadOffset(buf, w);
        width = w;
        recordsStart = Block.RecordsStart(w, numRestarts);
        return true;
    }

    /// <summary>
    /// Position at the first record whose key ≥ <paramref name="target"/> (the ceiling) in the block
    /// at <paramref name="blockStart"/>: predecessor-restart binary search, then a forward scan to
    /// <c>recordsEnd</c>. On a hit copies the ceiling key into <paramref name="keyBuf"/> and returns
    /// its value <see cref="Bound"/>. Returns <c>false</c> when the block is empty or every key is
    /// &lt; <paramref name="target"/>.
    /// </summary>
    /// <param name="deltaValue">In delta mode, the reconstructed absolute value of the ceiling record
    /// (see <paramref name="deltaValues"/>); otherwise 0.</param>
    /// <param name="deltaValues">When <c>true</c>, record values are read as RocksDB-style delta-coded
    /// integers (absolute at restart heads, deltas in between, see <see cref="BlockBuilder.AddDeltaValue"/>)
    /// and accumulated into <paramref name="deltaValue"/>. The binary search picks the rightmost restart
    /// with first key ≤ <paramref name="target"/>, so the ceiling is within that restart run or is exactly
    /// the head of the next run — the scan crosses at most one restart boundary, and that crossing record's
    /// restart-aligned index makes it re-anchor to an absolute value. Requires
    /// <paramref name="restartInterval"/> &gt; 0.</param>
    /// <param name="restartInterval">Records per restart run; only consulted in delta mode.</param>
    internal static bool SeekCeiling<TReader, TPin>(scoped in TReader reader, long blockStart,
        scoped ReadOnlySpan<byte> target, scoped Span<byte> keyBuf, out int keyLen, out Bound value,
        out long deltaValue, bool deltaValues = false, int restartInterval = 0)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        keyLen = 0;
        value = default;
        deltaValue = 0;
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

        // Delta-value accumulation: the scan starts at a restart head, so recordIndex tracks the global
        // record number; a record at a restart boundary (recordIndex % restartInterval == 0) carries an
        // absolute value, every other record a delta against the previous one.
        long recordIndex = scanRestart * restartInterval;
        long runningValue = 0;
        Span<byte> vbuf = stackalloc byte[sizeof(ulong)];

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

            if (deltaValues)
            {
                if (valueLen > 6) return false; // u48 ceiling — reject corruption before the shift widens it
                vbuf.Clear();
                if (valueLen > 0 && !reader.TryRead(valueSizeOffset + Block.SizePrefix, vbuf[..valueLen])) return false;
                long v = (long)BinaryPrimitives.ReadUInt64LittleEndian(vbuf);
                runningValue = recordIndex % restartInterval == 0 ? v : runningValue + v;
                recordIndex++;
            }

            if (target.SequenceCompareTo(keyBuf[..kLen]) <= 0)
            {
                keyLen = kLen;
                value = new Bound(valueSizeOffset + Block.SizePrefix, valueLen);
                deltaValue = runningValue;
                return true;
            }
            pos = valueSizeOffset + Block.SizePrefix + valueLen;
        }
        return false;
    }
}
