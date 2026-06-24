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

    /// <summary>Block-relative start and end of the index block's records at <paramref name="blockStart"/>.
    /// Returns <c>false</c> if the block is unreadable or not an index block. Used by
    /// <see cref="SortedTableEnumerator{TReader,TPin}"/> to walk the index in order.</summary>
    internal static bool TryReadRecordRange<TReader, TPin>(scoped in TReader reader, long blockStart,
        out long recordsStart, out long recordsEnd)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        recordsStart = 0;
        recordsEnd = 0;
        Header header = default;
        if (!reader.TryRead(blockStart, MemoryMarshal.AsBytes(new Span<Header>(ref header)))) return false;
        if (header.Flag != Block.FlagIndex) return false;
        recordsStart = Unsafe.SizeOf<Header>() + (long)sizeof(uint) * header.NumRestarts;
        recordsEnd = header.RecordsEnd;
        return true;
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
    /// scan starts at a restart record (<c>cp == 0</c>), whose absolute value anchors the running sum; the
    /// scan crosses at most one further restart, and that record's <c>cp == 0</c> re-anchors it to an
    /// absolute value.
    /// </remarks>
    internal static bool SeekCeiling<TReader, TPin>(scoped in TReader reader, long blockStart,
        scoped ReadOnlySpan<byte> target, scoped Span<byte> keyBuf,
        out int keyLen, out long byteOffset)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        keyLen = 0;
        byteOffset = 0;

        Header header = default;
        if (!reader.TryRead(blockStart, MemoryMarshal.AsBytes(new Span<Header>(ref header)))) return false;
        if (header.Flag != Block.FlagIndex || header.NumRestarts == 0) return false;

        long restartTableStart = blockStart + Unsafe.SizeOf<Header>();
        long end = blockStart + header.RecordsEnd;
        uint offset = 0;
        Block.RecordHeader rec = default;

        // Rightmost restart whose first key <= target (cp == 0 there, so the suffix is the full key).
        long lo = 0;
        long hi = header.NumRestarts - 1;
        long found = -1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (!reader.TryRead(restartTableStart + mid * sizeof(uint), MemoryMarshal.AsBytes(new Span<uint>(ref offset)))) return false;
            long recStart = blockStart + offset;
            if (!reader.TryRead(recStart, MemoryMarshal.AsBytes(new Span<Block.RecordHeader>(ref rec)))) return false;
            int firstKeyLen = rec.SuffixLength;
            using TPin keyPin = reader.PinBuffer(new Bound(recStart + 2, firstKeyLen));
            if (keyPin.Buffer.SequenceCompareTo(target) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // target < firstKey ⇒ ceiling is the very first record; clamp the scan start to restart 0.
        long scanRestart = found < 0 ? 0 : found;
        if (!reader.TryRead(restartTableStart + scanRestart * sizeof(uint), MemoryMarshal.AsBytes(new Span<uint>(ref offset)))) return false;
        long pos = blockStart + offset;

        // A restart record (cp == 0) carries an absolute value, every other record a delta against the
        // previous one. The scan starts at a restart, so the first record anchors the running sum.
        long runningValue = 0;
        Span<byte> vbuf = stackalloc byte[sizeof(ulong)];

        // Scan forward across restart boundaries (cp = 0 self-corrects) for the first key >= target.
        while (pos < end)
        {
            if (!reader.TryRead(pos, MemoryMarshal.AsBytes(new Span<Block.RecordHeader>(ref rec)))) return false;
            int cp = rec.CommonPrefix;
            int suffixLen = rec.SuffixLength;
            if (!reader.TryRead(pos + 2, keyBuf.Slice(cp, suffixLen))) return false; // keep [0..cp) from prev
            int kLen = cp + suffixLen;

            long valueSizeOffset = pos + 2 + suffixLen;
            byte valueLen = 0;
            if (!reader.TryRead(valueSizeOffset, new Span<byte>(ref valueLen))) return false;

            if (valueLen > 6) return false; // u48 ceiling — reject corruption before the shift widens it
            vbuf.Clear();
            if (valueLen > 0 && !reader.TryRead(valueSizeOffset + Block.SizePrefix, vbuf[..valueLen])) return false;
            long v = (long)BinaryPrimitives.ReadUInt64LittleEndian(vbuf);
            runningValue = cp == 0 ? v : runningValue + v;

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
