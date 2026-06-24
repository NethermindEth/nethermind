// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    // Previous index value; only used by AddFrontCodedValue to find which low bytes changed.
    private ulong _prevValue;
    private int _recordCount;

    public int RecordCount => _recordCount;

    /// <summary>Append a data record <c>[cp][suffixLen][valueLen][keySuffix][value]</c>. Keys must arrive
    /// in ascending order; key and value lengths ≤ 255.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        int cp = StartRecord(key);
        Block.DataRecordHeader h = new((byte)cp, (byte)(key.Length - cp), (byte)value.Length);
        _body.AddRange(MemoryMarshal.AsBytes(new Span<Block.DataRecordHeader>(ref h)));
        _body.AddRange(key[cp..]);
        _body.AddRange(value);
        EndRecord(key);
    }

    /// <summary>Append an index record whose value is a non-negative 48-bit integer (a data-block byte
    /// offset). Only the low little-endian bytes that differ from the previous record's value are stored —
    /// <c>[cp][suffixLen][valChangedLen][keySuffix][valChanged]</c> — and the reader keeps the unchanged
    /// high bytes. At a key restart (<c>cp == 0</c>) the value is coded against 0 (fully restated) so a
    /// seek can begin there.</summary>
    /// <remarks>Offsets ascend, so the high bytes rarely change and the stored low-byte prefix stays short;
    /// the little-endian layout lets <see cref="IndexBlockReader.SeekCeiling"/> copy those bytes straight
    /// onto the low end of a running value.</remarks>
    public void AddFrontCodedValue(scoped ReadOnlySpan<byte> key, long value)
    {
        Debug.Assert((ulong)value >> 48 == 0, "index value must fit in 48 bits");
        int cp = StartRecord(key);

        // Number of low (little-endian) bytes that differ from the previous value (against 0 at a restart):
        // the byte index of the highest-order change plus one; value/diff 0 ⇒ nothing stored.
        ulong v = (ulong)value;
        ulong diff = v ^ (cp == 0 ? 0UL : _prevValue);
        int changedLen = diff == 0 ? 0 : BitOperations.Log2(diff) / 8 + 1;

        Block.IndexRecordHeader h = new((byte)cp, (byte)(key.Length - cp), (byte)changedLen);
        _body.AddRange(MemoryMarshal.AsBytes(new Span<Block.IndexRecordHeader>(ref h)));
        _body.AddRange(key[cp..]);
        _body.AddRange(MemoryMarshal.AsBytes(new Span<ulong>(ref v))[..changedLen]);

        _prevValue = v;
        EndRecord(key);
    }

    /// <summary>Determine the record's key common-prefix length and record a restart-table entry at every
    /// <c>cp == 0</c>. A record is a <em>restart</em> when <c>cp == 0</c> (it stores a full key): forced
    /// every <c>restartInterval</c> records to bound scan length, and arising naturally wherever the key
    /// shares no leading byte with its predecessor.</summary>
    private int StartRecord(scoped ReadOnlySpan<byte> key)
    {
        int cp = _recordCount % restartInterval == 0
            ? 0
            : ((ReadOnlySpan<byte>)_prevKey.AsSpan()).CommonPrefixLength(key);
        if (cp == 0)
            _restarts.Add(_body.Count);
        return cp;
    }

    /// <summary>Advance the previous-key and record-count state after a record's bytes have been written.</summary>
    private void EndRecord(scoped ReadOnlySpan<byte> key)
    {
        _prevKey.Clear();
        _prevKey.AddRange(key);
        _recordCount++;
    }

    /// <summary>Whether adding a record of the given key/value lengths would push the finished block
    /// (assuming the 2-byte width that any ≤ 64 KiB block uses) across a 4 KiB page. Used by the data-block
    /// size cap so each block stays within one page; the index block is never capped.</summary>
    public bool WouldCrossPage(int keyLen, int valueLen)
    {
        // The next record may be a restart (cp == 0), which adds a restart-table entry; count it.
        long header = Block.RecordsStart(2, _restarts.Count + 1);
        int recordMax = Unsafe.SizeOf<Block.DataRecordHeader>() + keyLen + valueLen;
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
        _prevValue = 0;
        _recordCount = 0;
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
