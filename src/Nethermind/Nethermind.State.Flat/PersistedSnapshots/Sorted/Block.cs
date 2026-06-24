// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

/// <summary>
/// A single, self-describing, binary-searchable block of front-coded key/value records — the shared
/// unit of both the data blocks and the top-level index of a <see cref="SortedTable"/>.
/// </summary>
/// <remarks>
/// Wire layout (offsets relative to the block start):
/// <code>
///   [formatFlag u8]                     ; Block ⇒ W = 2, Index ⇒ W = 4 (offset width in bytes)
///   [recordsEnd  : W]                   ; block-relative byte offset where records end (content size)
///   [numRestarts : W]
///   [restartOffset : W × numRestarts]   ; block-relative; restartOffset[0] = 1 + 2W + W·numRestarts
///   [records...]                        ; [cp u8][suffixLen u8][keySuffix][vs u8][value]
/// </code>
/// Keys are front-coded against the previous record, resetting (<c>cp = 0</c>, full key) every
/// <c>restartInterval</c> records and at the block start — these are the <em>restarts</em>. The
/// header <c>formatFlag</c> records the block's role and thereby its offset width — a data
/// <c>Block</c> (capped well under 64 KiB) uses 2-byte offsets, the multi-MB <c>Index</c> uses
/// 4-byte — so one format serves both. <see cref="BlockReader.SeekCeiling"/> binary searches the
/// restarts then scans to <c>recordsEnd</c> for the first key ≥ the target (LevelDB
/// <c>Block::Iter::Seek</c>).
/// </remarks>
internal static class Block
{
    /// <summary>Width of the single-byte record fields (common-prefix, key-suffix size, value size).</summary>
    internal const int SizePrefix = sizeof(byte);

    // On-disk header flag selecting the block's role and thereby its offset width. A data Block is
    // capped at BlockSize (well under 64 KiB) so it uses 2-byte offsets; the Index can be multi-MB
    // and uses 4-byte offsets — one format serves both.
    internal const byte FlagBlock = 1;   // 2-byte offsets
    internal const byte FlagIndex = 2;   // 4-byte offsets

    /// <summary>Offset width in bytes for <paramref name="flag"/>, or 0 if it is neither
    /// <see cref="FlagBlock"/> nor <see cref="FlagIndex"/>.</summary>
    internal static int WidthFromFlag(byte flag) => flag switch
    {
        FlagBlock => 2,
        FlagIndex => 4,
        _ => 0,
    };

    /// <summary>Block-relative byte offset of the first record, given the offset width and restart count.</summary>
    internal static long RecordsStart(int width, long numRestarts) => 1 + 2L * width + (long)width * numRestarts;

    internal static long ReadOffset(scoped ReadOnlySpan<byte> src, int width) =>
        width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(src) : BinaryPrimitives.ReadUInt32LittleEndian(src);
}

/// <summary>
/// Builds one <see cref="Block"/>: records are added in ascending key order, front-coded and
/// restart-tracked off-heap, then emitted to a writer at <see cref="Finish"/> under the caller's
/// <see cref="Block.FlagBlock"/>/<see cref="Block.FlagIndex"/> role flag, which fixes the offset width.
/// </summary>
internal sealed class BlockBuilder(int restartInterval, int expectedBytes = 4096) : IDisposable
{
    private readonly NativeMemoryList<byte> _body = new(Math.Max(64, expectedBytes));
    private readonly NativeMemoryList<int> _restarts = new(64);
    private readonly NativeMemoryList<byte> _prevKey = new(256);
    private int _recordCount;
    // Previous record's absolute value, tracked only when records are added through AddDeltaValue.
    private long _prevValue;

    public int RecordCount => _recordCount;

    /// <summary>Append a record. Keys must arrive in ascending order; key and value lengths ≤ 255.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        WriteKey(key);
        _body.Add((byte)value.Length);
        _body.AddRange(value);
    }

    /// <summary>Append a record whose value is a non-negative integer, RocksDB-style: the absolute value at
    /// every restart head and the delta against the previous record's value in between, each stored as a
    /// minimal-width little-endian integer in the record's value slot (a zero stored value occupies no bytes
    /// and reads back as 0). Values must be non-decreasing — the index block's byte offsets ascend — and fit
    /// in 48 bits.</summary>
    /// <remarks>The reader (<see cref="BlockReader.SeekCeiling"/> in delta mode) reconstructs the absolute
    /// value by re-summing from the restart head it lands on; the absolute-at-restart anchoring is what lets
    /// a seek begin at any restart.</remarks>
    public void AddDeltaValue(scoped ReadOnlySpan<byte> key, long value)
    {
        Debug.Assert((ulong)value >> 48 == 0, "index value must fit in 48 bits");
        bool restart = _recordCount % restartInterval == 0;
        Debug.Assert(restart || value >= _prevValue, "delta-coded values must be non-decreasing");
        WriteKey(key);

        long stored = restart ? value : value - _prevValue;
        _prevValue = value;

        Span<byte> buf = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, (ulong)stored);
        int width = stored == 0 ? 0 : BitOperations.Log2((ulong)stored) / 8 + 1;
        _body.Add((byte)width);
        _body.AddRange(buf[..width]);
    }

    /// <summary>Write a record's front-coded key prefix (<c>[cp][suffixLen][keySuffix]</c>), opening a new
    /// restart run every <c>restartInterval</c> records, then advance the previous-key and record-count
    /// state. The caller appends the value bytes.</summary>
    private void WriteKey(scoped ReadOnlySpan<byte> key)
    {
        int cp;
        if (_recordCount % restartInterval == 0)
        {
            _restarts.Add(_body.Count);
            cp = 0;
        }
        else
        {
            cp = ((ReadOnlySpan<byte>)_prevKey.AsSpan()).CommonPrefixLength(key);
        }

        Span<byte> hdr = stackalloc byte[2];
        hdr[0] = (byte)cp;
        hdr[1] = (byte)(key.Length - cp);
        _body.AddRange(hdr);
        _body.AddRange(key[cp..]);

        _prevKey.Clear();
        _prevKey.AddRange(key);
        _recordCount++;
    }

    /// <summary>Whether adding a record of the given key/value lengths would push the finished block
    /// (assuming the 2-byte width that any ≤ 64 KiB block uses) across a 4 KiB page. Used by the data-block
    /// size cap so each block stays within one page; the index block is never capped.</summary>
    public bool WouldCrossPage(int keyLen, int valueLen)
    {
        int nRestarts = _restarts.Count + (_recordCount % restartInterval == 0 ? 1 : 0);
        long header = Block.RecordsStart(2, nRestarts);
        int recordMax = 2 + keyLen + Block.SizePrefix + valueLen;
        return header + _body.Count + recordMax > PageLayout.PageSize;
    }

    /// <summary>Emit the finished block under <paramref name="formatFlag"/>
    /// (<see cref="Block.FlagBlock"/> for a data block, <see cref="Block.FlagIndex"/> for the index)
    /// to <paramref name="writer"/>; returns the bytes written. The flag fixes the offset width — a
    /// data block is capped well under 64 KiB so its 2-byte offsets always fit.</summary>
    public long Finish<TWriter>(ref TWriter writer, byte formatFlag) where TWriter : IByteBufferWriter
    {
        int width = Block.WidthFromFlag(formatFlag);
        int n = _restarts.Count;
        int bodyLen = _body.Count;
        long recordsStart = Block.RecordsStart(width, n);
        long recordsEnd = recordsStart + bodyLen;

        long start = writer.Written;
        writer.GetSpan(1)[0] = formatFlag;
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
        _prevKey.Clear();
        _recordCount = 0;
        _prevValue = 0;
    }

    public void Dispose()
    {
        _body.Dispose();
        _restarts.Dispose();
        _prevKey.Dispose();
    }

    private static void WriteOffset<TWriter>(ref TWriter writer, int width, long value) where TWriter : IByteBufferWriter
    {
        Span<byte> dst = writer.GetSpan(width);
        if (width == 2) BinaryPrimitives.WriteUInt16LittleEndian(dst, checked((ushort)value));
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
