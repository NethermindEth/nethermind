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
    // Previous index value as min-width big-endian bytes; only used by AddFrontCodedValue.
    private readonly NativeMemoryList<byte> _prevValue = new(8);
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
    /// offset), front-coded as a min-width big-endian integer against the previous record's value:
    /// <c>[cp][suffixLen][valCp][valSuffixLen][keySuffix][valSuffix]</c>, where <c>valCp</c> is the leading
    /// bytes shared with the previous value. The value is fully restated (<c>valCp == 0</c>) at every key
    /// restart (<c>cp == 0</c>) so a seek can begin there.</summary>
    /// <remarks>Offsets ascend, so nearby values share their high (big-endian leading) bytes;
    /// <see cref="IndexBlockReader.SeekCeiling"/> reconstructs each by keeping the shared prefix from the
    /// running value and appending the suffix.</remarks>
    public void AddFrontCodedValue(scoped ReadOnlySpan<byte> key, long value)
    {
        Debug.Assert((ulong)value >> 48 == 0, "index value must fit in 48 bits");
        int cp = StartRecord(key);

        // Min-width big-endian value, dropping leading zero bytes; value 0 ⇒ empty span.
        Span<byte> be = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(be, (ulong)value);
        ReadOnlySpan<byte> valBE = be[(BitOperations.LeadingZeroCount((ulong)value) / 8)..];
        int valCp = cp == 0 ? 0 : ((ReadOnlySpan<byte>)_prevValue.AsSpan()).CommonPrefixLength(valBE);
        ReadOnlySpan<byte> valSuffix = valBE[valCp..];

        Block.IndexRecordHeader h = new((byte)cp, (byte)(key.Length - cp), (byte)valCp, (byte)valSuffix.Length);
        _body.AddRange(MemoryMarshal.AsBytes(new Span<Block.IndexRecordHeader>(ref h)));
        _body.AddRange(key[cp..]);
        _body.AddRange(valSuffix);

        _prevValue.Clear();
        _prevValue.AddRange(valBE);
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
        _prevValue.Clear();
        _recordCount = 0;
    }

    public void Dispose()
    {
        _body.Dispose();
        _restarts.Dispose();
        _prevKey.Dispose();
        _prevValue.Dispose();
    }

    private static void WriteOffset<TWriter>(ref TWriter writer, int width, long value) where TWriter : IByteBufferWriter
    {
        Span<byte> dst = writer.GetSpan(width);
        if (width == 2) BinaryPrimitives.WriteUInt16LittleEndian(dst, checked((ushort)value));
        else BinaryPrimitives.WriteUInt32LittleEndian(dst, checked((uint)value));
        writer.Advance(width);
    }
}
