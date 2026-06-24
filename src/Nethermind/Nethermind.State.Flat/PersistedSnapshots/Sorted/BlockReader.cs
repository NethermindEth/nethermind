// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>Read-side search and header parsing for a <see cref="Block"/>'s data records, whose values
/// are plain inline bytes. The index block's delta-coded values are read separately by
/// <see cref="IndexBlockReader"/>.</summary>
internal static class BlockReader
{
    /// <summary>Data-block header (<see cref="Block.FlagBlock"/>, 2-byte offsets): the role flag then
    /// the block-relative records-end and restart count. Read by reinterpreting the leading bytes (the
    /// <c>u16</c> fields are little-endian on disk, matching the host on supported targets).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct Header
    {
        internal readonly byte Flag;
        internal readonly ushort RecordsEnd;
        internal readonly ushort NumRestarts;
    }

    /// <summary>Parse the block header at <paramref name="blockStart"/>: offset width, the
    /// block-relative records-end, restart count, and the block-relative records start.</summary>
    /// <remarks>Width-agnostic; used by <see cref="SortedTableEnumerator{TReader,TPin}"/>, which walks
    /// data blocks without assuming a fixed width.</remarks>
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

        Header header = default;
        if (!reader.TryRead(blockStart, MemoryMarshal.AsBytes(new Span<Header>(ref header)))) return false;
        if (header.Flag != Block.FlagBlock || header.NumRestarts == 0) return false;

        long restartTableStart = blockStart + Unsafe.SizeOf<Header>();
        long end = blockStart + header.RecordsEnd;
        Span<byte> ob = stackalloc byte[sizeof(ushort)];
        Span<byte> rh = stackalloc byte[2]; // [cp u8][suffixLen u8]

        // Rightmost restart whose first key <= target (cp == 0 there, so the suffix is the full key).
        long lo = 0;
        long hi = header.NumRestarts - 1;
        long found = -1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (!reader.TryRead(restartTableStart + mid * sizeof(ushort), ob)) return false;
            long recStart = blockStart + MemoryMarshal.Read<ushort>(ob);
            if (!reader.TryRead(recStart, rh)) return false;
            int firstKeyLen = MemoryMarshal.Read<Block.RecordHeader>(rh).SuffixLength;
            using TPin keyPin = reader.PinBuffer(new Bound(recStart + 2, firstKeyLen));
            if (keyPin.Buffer.SequenceCompareTo(target) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // target < firstKey ⇒ ceiling is the very first record; clamp the scan start to restart 0.
        long scanRestart = found < 0 ? 0 : found;
        if (!reader.TryRead(restartTableStart + scanRestart * sizeof(ushort), ob)) return false;
        long pos = blockStart + MemoryMarshal.Read<ushort>(ob);

        // Scan forward across restart boundaries (cp = 0 self-corrects) for the first key >= target.
        while (pos < end)
        {
            if (!reader.TryRead(pos, rh)) return false;
            Block.RecordHeader record = MemoryMarshal.Read<Block.RecordHeader>(rh);
            int cp = record.CommonPrefix;
            int suffixLen = record.SuffixLength;
            if (!reader.TryRead(pos + 2, keyBuf.Slice(cp, suffixLen))) return false; // keep [0..cp) from prev
            int kLen = cp + suffixLen;

            long valueSizeOffset = pos + 2 + suffixLen;
            if (!reader.TryRead(valueSizeOffset, rh[..1])) return false;
            int valueLen = rh[0];

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
