// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>Read-side search and header parsing for a <see cref="Block"/>'s data records, whose values
/// are plain inline bytes. The index block's changed-prefix-coded values are read separately by
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
    /// <remarks>A data block normally fits within one <see cref="SortedTable.BlockSize"/> page, so the page
    /// is pinned once and the search runs over a <see cref="SpanByteReader"/> on the pinned span — a
    /// copy-on-pin reader does a single read instead of one per record and field. A block whose records
    /// reach past the pinned page (not expected for a well-formed data block) is searched straight through
    /// <paramref name="reader"/> instead, so it is still read in full.</remarks>
    internal static bool SeekCeiling<TReader, TPin>(scoped in TReader reader, long blockStart,
        scoped ReadOnlySpan<byte> target, scoped Span<byte> keyBuf, out int keyLen, out Bound value)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        keyLen = 0;
        value = default;

        long remaining = reader.Length - blockStart;
        if (remaining <= 0) return false;
        long pageLen = remaining < SortedTable.BlockSize ? remaining : SortedTable.BlockSize;

        using TPin pagePin = reader.PinBuffer(new Bound(blockStart, pageLen));
        SpanByteReader page = new(pagePin.Buffer);

        // Read and validate the header once from the pinned page; the core search reuses it.
        Header header = default;
        if (!page.TryRead(0, MemoryMarshal.AsBytes(new Span<Header>(ref header)))) return false;
        if (header.Flag != Block.FlagBlock || header.NumRestarts == 0) return false;

        // Whole block in the pinned page ⇒ search the span; otherwise fall back to the original reader.
        if (header.RecordsEnd <= pageLen)
        {
            if (!SeekCeilingCore<SpanByteReader, NoOpPin>(in page, 0, in header, target, keyBuf, out keyLen, out Bound local))
                return false;
            // local.Offset is page-relative; lift it back to reader-absolute for the caller.
            value = new Bound(blockStart + local.Offset, local.Length);
            return true;
        }

        return SeekCeilingCore<TReader, TPin>(in reader, blockStart, in header, target, keyBuf, out keyLen, out value);
    }

    /// <summary>Restart binary search then forward scan over the data block at <paramref name="blockStart"/>
    /// described by the already-parsed <paramref name="header"/> (<see cref="Block.FlagBlock"/>,
    /// <c>NumRestarts &gt; 0</c>), reading through <paramref name="reader"/>. The returned value
    /// <see cref="Bound"/> is in <paramref name="reader"/> coordinates.</summary>
    private static bool SeekCeilingCore<TReader, TPin>(scoped in TReader reader, long blockStart, in Header header,
        scoped ReadOnlySpan<byte> target, scoped Span<byte> keyBuf, out int keyLen, out Bound value)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        keyLen = 0;
        value = default;

        long restartTableStart = blockStart + Unsafe.SizeOf<Header>();
        long end = blockStart + header.RecordsEnd;

        // Pin the whole restart-offset table once and index it in place, instead of reading each offset.
        long offsetsBytes = (long)header.NumRestarts * sizeof(ushort);
        if (restartTableStart + offsetsBytes > reader.Length) return false;
        using TPin offsetsPin = reader.PinBuffer(new Bound(restartTableStart, offsetsBytes));
        ReadOnlySpan<ushort> offsets = MemoryMarshal.Cast<byte, ushort>(offsetsPin.Buffer);
        Block.DataRecordHeader rec = default;

        // Restart offsets are block-relative record starts; a corrupt one must land within the records region
        // [recordsStart, RecordsEnd), not in the header/restart table or past the block.
        int recordsStart = Unsafe.SizeOf<Header>() + (int)offsetsBytes;

        // Rightmost restart whose first key <= target (cp == 0 there, so the suffix is the full key).
        int lo = 0;
        int hi = header.NumRestarts - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            long recStart = blockStart + CheckRestartOffset(offsets[mid], recordsStart, header.RecordsEnd);
            if (!reader.TryRead(recStart, MemoryMarshal.AsBytes(new Span<Block.DataRecordHeader>(ref rec)))) return false;
            using TPin keyPin = reader.PinBuffer(new Bound(recStart + Unsafe.SizeOf<Block.DataRecordHeader>(), rec.SuffixLength));
            if (keyPin.Buffer.SequenceCompareTo(target) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // target < firstKey ⇒ ceiling is the very first record; clamp the scan start to restart 0.
        int scanRestart = found < 0 ? 0 : found;
        long pos = blockStart + CheckRestartOffset(offsets[scanRestart], recordsStart, header.RecordsEnd);

        // Scan forward across restart boundaries (cp = 0 self-corrects) for the first key >= target. The
        // 3-byte prefix blit carries the value length, so the value is just a Bound past the key.
        int prevKeyLen = 0;
        while (pos < end)
        {
            if (!reader.TryRead(pos, MemoryMarshal.AsBytes(new Span<Block.DataRecordHeader>(ref rec)))) return false;
            int cp = rec.CommonPrefix;
            int suffixLen = rec.SuffixLength;
            // Front-coding keeps keyBuf[0..cp) from the previous record; a cp beyond the previous key length
            // would rebuild the key from stale bytes (a silent wrong key). A restart record (cp == 0) always
            // passes and resets the running key.
            if (cp > prevKeyLen)
                SortedTable.ThrowCorrupt($"data record at byte {pos} declares common-prefix {cp} exceeding the previous key length {prevKeyLen}");
            if (cp + suffixLen > keyBuf.Length)
                SortedTable.ThrowCorrupt($"data record at byte {pos} declares key length {cp}+{suffixLen} exceeding the {keyBuf.Length}-byte reader buffer");
            long keyStart = pos + Unsafe.SizeOf<Block.DataRecordHeader>();
            if (!reader.TryRead(keyStart, keyBuf.Slice(cp, suffixLen))) return false; // keep [0..cp) from prev
            int kLen = cp + suffixLen;
            prevKeyLen = kLen;
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

    /// <summary>Validate a block-relative restart offset lands within the records region
    /// <c>[recordsStart, recordsEnd)</c> before it is dereferenced; throws (wipe-and-resync) otherwise.</summary>
    private static ushort CheckRestartOffset(ushort offset, int recordsStart, ushort recordsEnd)
    {
        if (offset < recordsStart || offset >= recordsEnd)
            SortedTable.ThrowCorrupt($"data block restart offset {offset} is outside the records region [{recordsStart}, {recordsEnd})");
        return offset;
    }
}
