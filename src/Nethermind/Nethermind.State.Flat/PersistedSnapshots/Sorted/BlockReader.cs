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

    /// <summary>Block-relative start and end of the data block's records at <paramref name="blockStart"/>
    /// — the byte range a forward walk covers, after the header and restart table. Returns <c>false</c> if
    /// the block is unreadable or not a data block. Used by <see cref="SortedTableEnumerator{TReader,TPin}"/>
    /// to walk a data block without binary-searching it.</summary>
    internal static bool TryReadRecordRange<TReader, TPin>(scoped in TReader reader, long blockStart,
        out long recordsStart, out long recordsEnd)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        recordsStart = 0;
        recordsEnd = 0;
        Header header = default;
        if (!reader.TryRead(blockStart, MemoryMarshal.AsBytes(new Span<Header>(ref header)))) return false;
        if (header.Flag != Block.FlagBlock) return false;
        recordsStart = Unsafe.SizeOf<Header>() + (long)sizeof(ushort) * header.NumRestarts;
        recordsEnd = header.RecordsEnd;
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
        ushort offset = 0;
        Block.RecordHeader rec = default;

        // Rightmost restart whose first key <= target (cp == 0 there, so the suffix is the full key).
        long lo = 0;
        long hi = header.NumRestarts - 1;
        long found = -1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (!reader.TryRead(restartTableStart + mid * sizeof(ushort), MemoryMarshal.AsBytes(new Span<ushort>(ref offset)))) return false;
            long recStart = blockStart + offset;
            if (!reader.TryRead(recStart, MemoryMarshal.AsBytes(new Span<Block.RecordHeader>(ref rec)))) return false;
            int firstKeyLen = rec.SuffixLength;
            using TPin keyPin = reader.PinBuffer(new Bound(recStart + 2, firstKeyLen));
            if (keyPin.Buffer.SequenceCompareTo(target) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // target < firstKey ⇒ ceiling is the very first record; clamp the scan start to restart 0.
        long scanRestart = found < 0 ? 0 : found;
        if (!reader.TryRead(restartTableStart + scanRestart * sizeof(ushort), MemoryMarshal.AsBytes(new Span<ushort>(ref offset)))) return false;
        long pos = blockStart + offset;

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
