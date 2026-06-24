// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.PersistedSnapshots.Sorted;

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
    /// <remarks><see cref="IndexBlockReader.SeekCeiling"/> reconstructs the absolute value by re-summing
    /// from the restart head it lands on; the absolute-at-restart anchoring is what lets a seek begin at
    /// any restart.</remarks>
    public void AddDeltaValue(scoped ReadOnlySpan<byte> key, long value)
    {
        Debug.Assert((ulong)value >> 48 == 0, "index value must fit in 48 bits");
        // A restart (cp == 0) re-anchors to an absolute value; in between, the delta against the previous.
        bool restart = WriteKey(key) == 0;
        Debug.Assert(restart || value >= _prevValue, "delta-coded values must be non-decreasing");

        long stored = restart ? value : value - _prevValue;
        _prevValue = value;

        ulong v = (ulong)stored;
        int width = stored == 0 ? 0 : BitOperations.Log2(v) / 8 + 1;
        _body.Add((byte)width);
        _body.AddRange(MemoryMarshal.AsBytes(new Span<ulong>(ref v))[..width]);
    }

    /// <summary>Write a record's front-coded key prefix (<c>[cp][suffixLen][keySuffix]</c>), then advance
    /// the previous-key and record-count state; returns the record's common-prefix length <c>cp</c>. The
    /// caller appends the value bytes.</summary>
    /// <remarks>A record is a <em>restart</em> when <c>cp == 0</c> (it stores a full key): forced every
    /// <c>restartInterval</c> records to bound scan length, and arising naturally wherever the key shares
    /// no leading byte with its predecessor. Every restart records its offset, so the restart table indexes
    /// them all and the index block re-anchors its delta value at each (see <see cref="AddDeltaValue"/>).</remarks>
    private int WriteKey(scoped ReadOnlySpan<byte> key)
    {
        int cp = _recordCount % restartInterval == 0
            ? 0
            : ((ReadOnlySpan<byte>)_prevKey.AsSpan()).CommonPrefixLength(key);
        if (cp == 0)
            _restarts.Add(_body.Count);

        Block.RecordHeader rh = new((byte)cp, (byte)(key.Length - cp));
        _body.AddRange(MemoryMarshal.AsBytes(new Span<Block.RecordHeader>(ref rh)));
        _body.AddRange(key[cp..]);

        _prevKey.Clear();
        _prevKey.AddRange(key);
        _recordCount++;
        return cp;
    }

    /// <summary>Whether adding a record of the given key/value lengths would push the finished block
    /// (assuming the 2-byte width that any ≤ 64 KiB block uses) across a 4 KiB page. Used by the data-block
    /// size cap so each block stays within one page; the index block is never capped.</summary>
    public bool WouldCrossPage(int keyLen, int valueLen)
    {
        // The next record may be a restart (cp == 0), which adds a restart-table entry; count it.
        long header = Block.RecordsStart(2, _restarts.Count + 1);
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
