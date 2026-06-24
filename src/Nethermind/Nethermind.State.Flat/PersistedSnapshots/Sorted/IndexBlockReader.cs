// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// Read-side ceiling search over a <see cref="SortedTable"/> index block, whose record values are u48 byte
/// offsets stored little-endian as only the low bytes that changed from the previous value (the high bytes
/// carry over), reset at every restart (see <see cref="BlockBuilder.AddChangedPrefixValue"/>). The restart
/// binary search and forward scan are its own, since the index scan reconstructs the offset where the
/// data-block scan (<see cref="DataBlockReader.SeekCeiling"/>) returns a value <see cref="Bound"/>.
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
    /// scan starts at a restart record (<c>cp == 0</c>) that resets the running value to 0; each later
    /// record overwrites only its low <c>valChangedLen</c> bytes, keeping the unchanged high bytes.
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

        // The index block stays well under 2 GiB (≈ 60 MiB even for a 12 GiB snapshot), so its restart
        // count fits int and the restart indices below cannot overflow. The offset table can be large, so
        // each binary-search step reads just the one restart offset it needs rather than pinning the table.
        Block.IndexRecordHeader rec = default;

        // Rightmost restart whose first key <= target (cp == 0 there, so the suffix is the full key).
        int lo = 0;
        int hi = (int)header.NumRestarts - 1;
        int found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            uint offset = 0;
            if (!reader.TryRead(restartTableStart + (long)mid * sizeof(uint), MemoryMarshal.AsBytes(new Span<uint>(ref offset)))) return false;
            long recStart = blockStart + offset;
            if (!reader.TryRead(recStart, MemoryMarshal.AsBytes(new Span<Block.IndexRecordHeader>(ref rec)))) return false;
            using TPin keyPin = reader.PinBuffer(new Bound(recStart + Unsafe.SizeOf<Block.IndexRecordHeader>(), rec.SuffixLength));
            if (keyPin.Buffer.SequenceCompareTo(target) <= 0) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // target < firstKey ⇒ ceiling is the very first record; clamp the scan start to restart 0.
        int scanRestart = found < 0 ? 0 : found;
        uint scanOffset = 0;
        if (!reader.TryRead(restartTableStart + (long)scanRestart * sizeof(uint), MemoryMarshal.AsBytes(new Span<uint>(ref scanOffset)))) return false;
        long pos = blockStart + scanOffset;

        // The value (a u48 byte offset) is stored little-endian as only the low bytes that changed from the
        // previous record; a restart (cp == 0) drops the previous high bytes by resetting to 0. Each record
        // overwrites the running value's low valChangedLen bytes in place — a direct copy, no decode.
        long runningValue = 0;

        // Scan forward across restart boundaries (cp = 0 self-corrects) for the first key >= target.
        while (pos < end)
        {
            if (!reader.TryRead(pos, MemoryMarshal.AsBytes(new Span<Block.IndexRecordHeader>(ref rec)))) return false;
            int cp = rec.CommonPrefix;
            int suffixLen = rec.SuffixLength;
            int valChangedLen = rec.ValueChangedLength;
            if (valChangedLen > 6) return false; // > u48 ⇒ corrupt

            long keyStart = pos + Unsafe.SizeOf<Block.IndexRecordHeader>();
            if (!reader.TryRead(keyStart, keyBuf.Slice(cp, suffixLen))) return false; // keep [0..cp) from prev
            int kLen = cp + suffixLen;
            long valueStart = keyStart + suffixLen;

            if (cp == 0) runningValue = 0;
            if (valChangedLen > 0 &&
                !reader.TryRead(valueStart, MemoryMarshal.AsBytes(new Span<long>(ref runningValue))[..valChangedLen])) return false;

            if (target.SequenceCompareTo(keyBuf[..kLen]) <= 0)
            {
                keyLen = kLen;
                byteOffset = runningValue;
                return true;
            }
            pos = valueStart + valChangedLen;
        }
        return false;
    }
}
