// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// A single, self-describing, binary-searchable block of front-coded key/value records — the shared
/// unit of both the data blocks and the top-level index of a <see cref="SortedTable"/>.
/// </summary>
/// <remarks>
/// Wire layout (offsets relative to the block start):
/// <code>
///   [offsetWidth u8]                    ; W = 2 or 4 bytes
///   [recordsEnd  : W]                   ; block-relative byte offset where records end (content size)
///   [numRestarts : W]
///   [restartOffset : W × numRestarts]   ; block-relative; restartOffset[0] = 1 + 2W + W·numRestarts
///   [records...]                        ; [cp u8][suffixLen u8][keySuffix][vs u8][value]
/// </code>
/// Keys are front-coded against the previous record, resetting (<c>cp = 0</c>, full key) every
/// <c>restartInterval</c> records and at the block start — these are the <em>restarts</em>. The
/// per-block <c>offsetWidth</c> lets a small block (≤ 64 KiB, e.g. a 4 KiB data block) use 2-byte
/// offsets while a large block (e.g. the multi-MB index) uses 4-byte offsets, so one format serves
/// both. <see cref="BlockReader.SeekCeiling"/> binary searches the restarts then scans to
/// <c>recordsEnd</c> for the first key ≥ the target (LevelDB <c>Block::Iter::Seek</c>).
/// </remarks>
internal static class Block
{
    /// <summary>Width of the single-byte record fields (common-prefix, key-suffix size, value size).</summary>
    internal const int SizePrefix = sizeof(byte);

    internal const byte Width2 = 2;
    internal const byte Width4 = 4;

    /// <summary>Block-relative byte offset of the first record, given the offset width and restart count.</summary>
    internal static long RecordsStart(int width, long numRestarts) => 1 + 2L * width + (long)width * numRestarts;

    internal static long ReadOffset(scoped ReadOnlySpan<byte> src, int width) =>
        width == Width2 ? BinaryPrimitives.ReadUInt16LittleEndian(src) : BinaryPrimitives.ReadUInt32LittleEndian(src);
}

/// <summary>
/// Builds one <see cref="Block"/>: records are added in ascending key order, front-coded and
/// restart-tracked off-heap, then emitted to a writer at <see cref="Finish"/>, which picks the
/// narrowest offset width that fits the finished block.
/// </summary>
internal sealed class BlockBuilder(int restartInterval, int expectedBytes = 4096) : IDisposable
{
    private readonly NativeMemoryList<byte> _body = new(Math.Max(64, expectedBytes));
    private readonly NativeMemoryList<int> _restarts = new(64);
    private readonly byte[] _prevKey = new byte[256];
    private int _prevKeyLen;
    private int _recordCount;

    public int RecordCount => _recordCount;

    /// <summary>Append a record. Keys must arrive in ascending order; key and value lengths ≤ 255.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        int cp;
        if (_recordCount % restartInterval == 0)
        {
            _restarts.Add(_body.Count);
            cp = 0;
        }
        else
        {
            cp = ((ReadOnlySpan<byte>)_prevKey.AsSpan(0, _prevKeyLen)).CommonPrefixLength(key);
        }

        Span<byte> hdr = stackalloc byte[2];
        hdr[0] = (byte)cp;
        hdr[1] = (byte)(key.Length - cp);
        _body.AddRange(hdr);
        _body.AddRange(key[cp..]);
        hdr[0] = (byte)value.Length;
        _body.AddRange(hdr[..1]);
        _body.AddRange(value);

        key.CopyTo(_prevKey);
        _prevKeyLen = key.Length;
        _recordCount++;
    }

    /// <summary>Whether adding a record of the given key/value lengths would push the finished block
    /// (assuming the 2-byte width that any ≤ 64 KiB block uses) past <paramref name="contentLimit"/>.
    /// Used by the data-block size cap; the index block is never capped.</summary>
    public bool WouldExceedIfAdded(int keyLen, int valueLen, int contentLimit)
    {
        int nRestarts = _restarts.Count + (_recordCount % restartInterval == 0 ? 1 : 0);
        long header = Block.RecordsStart(Block.Width2, nRestarts);
        int recordMax = 2 + keyLen + Block.SizePrefix + valueLen;
        return header + _body.Count + recordMax > contentLimit;
    }

    /// <summary>Emit the finished block to <paramref name="writer"/>; returns the bytes written.</summary>
    public long Finish<TWriter>(ref TWriter writer) where TWriter : IByteBufferWriter
    {
        int n = _restarts.Count;
        int bodyLen = _body.Count;
        // bodyLen and n are width-independent, so a single trial-at-2 / fall-to-4 is exact.
        long end2 = Block.RecordsStart(Block.Width2, n) + bodyLen;
        int width = end2 <= ushort.MaxValue && n <= ushort.MaxValue ? Block.Width2 : Block.Width4;
        long recordsStart = Block.RecordsStart(width, n);
        long recordsEnd = recordsStart + bodyLen;

        long start = writer.Written;
        writer.GetSpan(1)[0] = (byte)width;
        writer.Advance(1);
        WriteOffset(ref writer, width, recordsEnd);
        WriteOffset(ref writer, width, n);
        Span<int> rs = _restarts.AsSpan();
        for (int k = 0; k < n; k++)
            WriteOffset(ref writer, width, recordsStart + rs[k]);
        IByteBufferWriter.Copy(ref writer, _body.AsSpan());
        return writer.Written - start;
    }

    public void Reset()
    {
        _body.Clear();
        _restarts.Clear();
        _prevKeyLen = 0;
        _recordCount = 0;
    }

    public void Dispose()
    {
        _body.Dispose();
        _restarts.Dispose();
    }

    private static void WriteOffset<TWriter>(ref TWriter writer, int width, long value) where TWriter : IByteBufferWriter
    {
        Span<byte> dst = writer.GetSpan(width);
        if (width == Block.Width2) BinaryPrimitives.WriteUInt16LittleEndian(dst, checked((ushort)value));
        else BinaryPrimitives.WriteUInt32LittleEndian(dst, checked((uint)value));
        writer.Advance(width);
    }
}

/// <summary>Read-side search and header parsing for a <see cref="Block"/>.</summary>
internal static class BlockReader
{
    /// <summary>Parse the block header at <paramref name="blockStart"/>: offset width, the
    /// block-relative records-end, restart count, and the block-relative records start.</summary>
    internal static bool ReadHeader<TReader, TPin>(scoped in TReader reader, long blockStart,
        out int width, out long recordsEnd, out long numRestarts, out long recordsStart)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
    {
        width = 0;
        recordsEnd = 0;
        numRestarts = 0;
        recordsStart = 0;

        Span<byte> buf = stackalloc byte[4];
        if (!reader.TryRead(blockStart, buf[..1])) return false;
        int w = buf[0];
        if (w != Block.Width2 && w != Block.Width4) return false;
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
    internal static bool SeekCeiling<TReader, TPin>(scoped in TReader reader, long blockStart,
        scoped ReadOnlySpan<byte> target, scoped Span<byte> keyBuf, out int keyLen, out Bound value)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
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
