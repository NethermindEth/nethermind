// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds a <see cref="IndexType.TwoByteSlotValue"/> HSST: fixed 2-byte keys, variable
/// values, packed start-offset trailer. Keys are added in strictly ascending byte order.
///
/// Output:
/// <c>[Value_0]…[Value_{N-1}][Offset_1: u16 LE]…[Offset_{N-1}: u16 LE][Key_0: 2 bytes]…[Key_{N-1}: 2 bytes][KeyCount: u16 LE = N − 1][IndexType: u8 = 0x05]</c>.
///
/// <c>Offset_i</c> is the start offset of <c>Value_i</c> measured from byte 0 of the
/// HSST (= first data byte). <c>Offset_0</c> is omitted because it is always 0;
/// <c>Offset_N</c> (one-past-end of the data region) is derived by the reader from the
/// trailer length. Hence per-entry value bounds are <c>[Offset_i, Offset_{i+1})</c>.
///
/// Fixed u16 offsets cap the cumulative data region at <c>ushort.MaxValue</c>
/// (65,535 bytes). <see cref="Build"/> throws when the cap is exceeded — the caller
/// is expected to gate on <see cref="FitsInOffsetWidth"/> before choosing this format.
/// </summary>
public ref struct HsstTwoByteSlotValueBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Fixed key length for this format. Single 2-byte slot suffix.</summary>
    public const int KeyLength = 2;
    /// <summary>Maximum addressable data-region size with u16 offsets.</summary>
    public const int MaxDataBytes = ushort.MaxValue;
    /// <summary>Maximum number of entries (<c>KeyCount</c> stores <c>N − 1</c> in a u16).</summary>
    public const int MaxEntries = 65536;

    private const int InitialCapacity = 16;

    private ref TWriter _writer;
    private readonly long _baseOffset;
    private long _writtenBeforeValue;
    private int _count;
    private ushort[]? _starts;
    private byte[]? _keys;

    public HsstTwoByteSlotValueBuilder(ref TWriter writer)
    {
        _writer = ref writer;
        _baseOffset = _writer.Written;
        _count = 0;
    }

    public void Dispose()
    {
        if (_starts is not null) { ArrayPool<ushort>.Shared.Return(_starts); _starts = null; }
        if (_keys is not null) { ArrayPool<byte>.Shared.Return(_keys); _keys = null; }
    }

    /// <summary>
    /// Pre-check whether a planned data-region size fits this format's u16 offset cap.
    /// Callers use this to decide between <see cref="HsstTwoByteSlotValueBuilder{TWriter}"/>
    /// and a wider-offset fallback (e.g. <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/>).
    /// </summary>
    public static bool FitsInOffsetWidth(long totalValueBytes)
        => (ulong)totalValueBytes <= ushort.MaxValue;

    /// <summary>
    /// Begin writing a value. After writing the value bytes via the returned writer,
    /// call <see cref="FinishValueWrite"/> with the entry's 2-byte key.
    /// </summary>
    public ref TWriter BeginValueWrite()
    {
        _writtenBeforeValue = _writer.Written;
        return ref _writer;
    }

    /// <summary>
    /// Finish a value previously begun with <see cref="BeginValueWrite"/>. <paramref name="key"/>
    /// must be exactly 2 bytes and strictly greater (byte-lex) than every previously
    /// written key.
    /// </summary>
    public void FinishValueWrite(scoped ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength)
            throw new ArgumentException($"TwoByteSlotValue requires {KeyLength}-byte keys; got length {key.Length}", nameof(key));

        EnsureCapacity(_count + 1);

        if (_count > 0)
        {
            ReadOnlySpan<byte> prev = _keys.AsSpan((_count - 1) * KeyLength, KeyLength);
            if (key.SequenceCompareTo(prev) <= 0)
                throw new ArgumentException($"Keys must be strictly ascending; got 0x{key[0]:X2}{key[1]:X2} after 0x{prev[0]:X2}{prev[1]:X2}", nameof(key));
        }

        long start = _writtenBeforeValue - _baseOffset;
        if ((ulong)start > ushort.MaxValue)
            throw new InvalidOperationException($"TwoByteSlotValue data region exceeded {MaxDataBytes} bytes at entry {_count}");

        _starts![_count] = (ushort)start;
        key.CopyTo(_keys.AsSpan(_count * KeyLength, KeyLength));
        _count++;
    }

    /// <summary>Convenience: write a (key, value) pair in one call.</summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        _writtenBeforeValue = _writer.Written;
        IByteBufferWriter.Copy(ref _writer, value);
        FinishValueWrite(key);
    }

    private void EnsureCapacity(int needed)
    {
        int current = _starts?.Length ?? 0;
        if (needed <= current) return;

        int newCap = current == 0 ? InitialCapacity : current * 2;
        if (newCap < needed) newCap = needed;
        if (newCap > MaxEntries) newCap = MaxEntries;
        if (needed > newCap)
            throw new InvalidOperationException($"TwoByteSlotValue entry count exceeded {MaxEntries}");

        ushort[] newStarts = ArrayPool<ushort>.Shared.Rent(newCap);
        byte[] newKeys = ArrayPool<byte>.Shared.Rent(newCap * KeyLength);
        if (_starts is not null)
        {
            Array.Copy(_starts, newStarts, _count);
            Array.Copy(_keys!, newKeys, _count * KeyLength);
            ArrayPool<ushort>.Shared.Return(_starts);
            ArrayPool<byte>.Shared.Return(_keys!);
        }
        _starts = newStarts;
        _keys = newKeys;
    }

    /// <summary>
    /// Append the trailer (<c>[Offsets][Keys][KeyCount][IndexType]</c>). The writer is
    /// already advanced through every value at this point. Throws on empty maps and on
    /// data-region overflow.
    /// </summary>
    public void Build()
    {
        int n = _count;
        if (n == 0)
            throw new InvalidOperationException("TwoByteSlotValue cannot encode an empty map; the caller must omit Build for zero-entry maps");

        long dataSize = _writer.Written - _baseOffset;
        if ((ulong)dataSize > ushort.MaxValue)
            throw new InvalidOperationException($"TwoByteSlotValue data region {dataSize} bytes exceeds {MaxDataBytes}");

        // Offsets: N − 1 u16 LE values (Offset_1..Offset_{N-1}); Offset_0 is omitted.
        int offsetsBytes = (n - 1) * 2;
        if (offsetsBytes > 0)
        {
            Span<byte> offsetsSpan = _writer.GetSpan(offsetsBytes);
            for (int i = 1; i < n; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(offsetsSpan[((i - 1) * 2)..], _starts![i]);
            _writer.Advance(offsetsBytes);
        }

        // Keys: N · 2 bytes, byte-reversed on the way out (LE-stored convention — a native
        // u16 load over a stored key now recovers the BE numeric value, letting SIMD
        // scans compare numerically; see HsstTwoByteKeySearch). _keys is logical (BE)
        // during build for the strict-ascending compare in FinishValueWrite.
        int keysBytes = n * KeyLength;
        Span<byte> keysSpan = _writer.GetSpan(keysBytes);
        ReadOnlySpan<byte> logicalKeys = _keys.AsSpan(0, keysBytes);
        for (int i = 0; i < n; i++)
        {
            keysSpan[i * 2 + 0] = logicalKeys[i * 2 + 1];
            keysSpan[i * 2 + 1] = logicalKeys[i * 2 + 0];
        }
        _writer.Advance(keysBytes);

        // Trailer: KeyCount (N − 1) u16 LE + IndexType byte.
        Span<byte> trailer = _writer.GetSpan(3);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer, (ushort)(n - 1));
        trailer[2] = (byte)IndexType.TwoByteSlotValue;
        _writer.Advance(3);
    }
}
