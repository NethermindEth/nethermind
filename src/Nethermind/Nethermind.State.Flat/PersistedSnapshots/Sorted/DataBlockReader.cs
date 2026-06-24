// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>Read-side search and header parsing for a <see cref="Block"/>'s data records, whose values
/// are plain inline bytes. The index block's front-coded values are read separately by
/// <see cref="IndexBlockReader"/>.</summary>
internal static class DataBlockReader
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

        // Pin the whole restart-offset table once and index it in place, instead of reading each offset.
        long offsetsBytes = (long)header.NumRestarts * sizeof(ushort);
        if (restartTableStart + offsetsBytes > reader.Length) return false;
        using TPin offsetsPin = reader.PinBuffer(new Bound(restartTableStart, offsetsBytes));
        ReadOnlySpan<ushort> offsets = MemoryMarshal.Cast<byte, ushort>(offsetsPin.Buffer);
        Block.DataRecordHeader rec = default;

        // Rightmost restart whose first key <= target (cp == 0 there, so the suffix is the full key).
        int lo = 0;
        int hi = header.NumRestarts - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            long recStart = blockStart + offsets[mid];
            if (!reader.TryRead(recStart, MemoryMarshal.AsBytes(new Span<Block.DataRecordHeader>(ref rec)))) return false;
            using TPin keyPin = reader.PinBuffer(new Bound(recStart + Unsafe.SizeOf<Block.DataRecordHeader>(), rec.SuffixLength));
            if (keyPin.Buffer.SequenceCompareTo(target) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // target < firstKey ⇒ ceiling is the very first record; clamp the scan start to restart 0.
        int scanRestart = found < 0 ? 0 : found;
        long pos = blockStart + offsets[scanRestart];

        // Scan forward across restart boundaries (cp = 0 self-corrects) for the first key >= target. The
        // 3-byte prefix blit carries the value length, so the value is just a Bound past the key.
        while (pos < end)
        {
            if (!reader.TryRead(pos, MemoryMarshal.AsBytes(new Span<Block.DataRecordHeader>(ref rec)))) return false;
            int cp = rec.CommonPrefix;
            int suffixLen = rec.SuffixLength;
            long keyStart = pos + Unsafe.SizeOf<Block.DataRecordHeader>();
            if (!reader.TryRead(keyStart, keyBuf.Slice(cp, suffixLen))) return false; // keep [0..cp) from prev
            int kLen = cp + suffixLen;
            long valueStart = keyStart + suffixLen;

            if (target.SequenceCompareTo(keyBuf[..kLen]) <= 0)
            {
                keyLen = kLen;
                value = new Bound(valueStart, rec.ValueLength);
                return true;
            }
            pos = valueStart + rec.ValueLength;
        }
        return false;
    }
}
