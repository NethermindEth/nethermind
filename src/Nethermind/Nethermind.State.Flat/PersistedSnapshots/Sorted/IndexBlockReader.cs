// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Read-side ceiling search over a <see cref="SortedTable"/> index block, whose record values are
/// RocksDB-style delta-coded u48 byte offsets — absolute at restart heads, deltas in between (see
/// <see cref="BlockBuilder.AddDeltaValue"/>). Reuses <see cref="Block.RecordHeader"/> for the per-record
/// key prefix; the restart binary search and forward scan are its own, since the index scan reconstructs
/// an absolute offset where the data-block scan (<see cref="BlockReader.SeekCeiling"/>) returns a value
/// <see cref="Bound"/>.
/// </summary>
internal static class IndexBlockReader
{
    /// <summary>Index-block header (<see cref="Block.FlagIndex"/>, 4-byte offsets): the role flag then
    /// the block-relative records-end and restart count. Read by reinterpreting the leading bytes (the
    /// <c>u32</c> fields are little-endian on disk, matching the host on supported targets).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct Header
    {
        internal readonly byte Flag;
        internal readonly uint RecordsEnd;
        internal readonly uint NumRestarts;
    }

    /// <summary>
    /// Position at the first separator ≥ <paramref name="target"/> (the ceiling) in the index block at
    /// <paramref name="blockStart"/> and return the reconstructed absolute byte offset
    /// <paramref name="byteOffset"/> of its data block, copying the ceiling separator into
    /// <paramref name="keyBuf"/>. Returns <c>false</c> when the index is empty or every separator is
    /// &lt; <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// The binary search picks the rightmost restart whose first key ≤ <paramref name="target"/>, so the
    /// ceiling lies within that restart run or is exactly the head of the next run — the scan crosses at
    /// most one restart boundary, and that crossing record's restart-aligned index makes it re-anchor to
    /// an absolute value. Requires <paramref name="restartInterval"/> &gt; 0.
    /// </remarks>
    internal static bool SeekCeiling<TReader, TPin>(scoped in TReader reader, long blockStart,
        scoped ReadOnlySpan<byte> target, scoped Span<byte> keyBuf, int restartInterval,
        out int keyLen, out long byteOffset)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        keyLen = 0;
        byteOffset = 0;

        Span<byte> hbuf = stackalloc byte[Unsafe.SizeOf<Header>()];
        if (!reader.TryRead(blockStart, hbuf)) return false;
        Header header = MemoryMarshal.Read<Header>(hbuf);
        if (header.Flag != Block.FlagIndex || header.NumRestarts == 0) return false;

        long restartTableStart = blockStart + Unsafe.SizeOf<Header>();
        long end = blockStart + header.RecordsEnd;
        Span<byte> ob = stackalloc byte[sizeof(uint)];
        Span<byte> rh = stackalloc byte[2]; // [cp u8][suffixLen u8]

        // Rightmost restart whose first key <= target (cp == 0 there, so the suffix is the full key).
        long lo = 0;
        long hi = header.NumRestarts - 1;
        long found = -1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (!reader.TryRead(restartTableStart + mid * sizeof(uint), ob)) return false;
            long recStart = blockStart + MemoryMarshal.Read<uint>(ob);
            if (!reader.TryRead(recStart, rh)) return false;
            int firstKeyLen = MemoryMarshal.Read<Block.RecordHeader>(rh).SuffixLength;
            using TPin keyPin = reader.PinBuffer(new Bound(recStart + 2, firstKeyLen));
            if (keyPin.Buffer.SequenceCompareTo(target) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // target < firstKey ⇒ ceiling is the very first record; clamp the scan start to restart 0.
        long scanRestart = found < 0 ? 0 : found;
        if (!reader.TryRead(restartTableStart + scanRestart * sizeof(uint), ob)) return false;
        long pos = blockStart + MemoryMarshal.Read<uint>(ob);

        // The scan starts at a restart head, so recordIndex tracks the global record number; a record at
        // a restart boundary (recordIndex % restartInterval == 0) carries an absolute value, every other
        // record a delta against the previous one.
        long recordIndex = scanRestart * restartInterval;
        long runningValue = 0;
        Span<byte> vbuf = stackalloc byte[sizeof(ulong)];

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

            if (valueLen > 6) return false; // u48 ceiling — reject corruption before the shift widens it
            vbuf.Clear();
            if (valueLen > 0 && !reader.TryRead(valueSizeOffset + Block.SizePrefix, vbuf[..valueLen])) return false;
            long v = (long)BinaryPrimitives.ReadUInt64LittleEndian(vbuf);
            runningValue = recordIndex % restartInterval == 0 ? v : runningValue + v;
            recordIndex++;

            if (target.SequenceCompareTo(keyBuf[..kLen]) <= 0)
            {
                keyLen = kLen;
                byteOffset = runningValue;
                return true;
            }
            pos = valueSizeOffset + Block.SizePrefix + valueLen;
        }
        return false;
    }
}
