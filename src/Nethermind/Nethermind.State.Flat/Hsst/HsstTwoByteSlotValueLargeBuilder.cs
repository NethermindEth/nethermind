// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds a <see cref="IndexType.TwoByteSlotValueLarge"/> HSST: wider sibling of
/// <see cref="HsstTwoByteSlotValueBuilder{TWriter}"/>. Same wire shape but u24 LE
/// start offsets, raising the data-region cap from 64 KiB to ~16 MiB. Keys are
/// added in strictly ascending byte order.
///
/// Output:
/// <c>[Value_0]…[Value_{N-1}][Offset_1: u24 LE]…[Offset_{N-1}: u24 LE][Key_0: 2 bytes]…[Key_{N-1}: 2 bytes][KeyCount: u16 LE = N − 1][IndexType: u8 = 0x06]</c>.
///
/// <c>Offset_0</c> is omitted (always 0); <c>Offset_N</c> (one-past-end of the data
/// region) is derived by the reader from the trailer length.
/// </summary>
public ref struct HsstTwoByteSlotValueLargeBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Fixed key length for this format. Single 2-byte slot suffix.</summary>
    public const int KeyLength = 2;
    /// <summary>Width on disk of each start offset (low 3 bytes of a u32, LE).</summary>
    public const int OffsetSize = 3;
    /// <summary>Maximum addressable data-region size with u24 offsets.</summary>
    public const int MaxDataBytes = (1 << 24) - 1;
    /// <summary>Maximum number of entries (<c>KeyCount</c> stores <c>N − 1</c> in a u16).</summary>
    public const int MaxEntries = 65536;

    private const int InitialCapacity = 16;

    private ref TWriter _writer;
    private readonly long _baseOffset;
    private long _writtenBeforeValue;
    private int _count;
    private uint[]? _starts;
    private byte[]? _keys;

    public HsstTwoByteSlotValueLargeBuilder(ref TWriter writer)
    {
        _writer = ref writer;
        _baseOffset = _writer.Written;
        _count = 0;
    }

    public void Dispose()
    {
        if (_starts is not null) { ArrayPool<uint>.Shared.Return(_starts); _starts = null; }
        if (_keys is not null) { ArrayPool<byte>.Shared.Return(_keys); _keys = null; }
    }

    /// <summary>
    /// Pre-check whether a planned data-region size fits this format's u24 offset cap.
    /// </summary>
    public static bool FitsInOffsetWidth(long totalValueBytes)
        => (ulong)totalValueBytes <= MaxDataBytes;

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
            throw new ArgumentException($"TwoByteSlotValueLarge requires {KeyLength}-byte keys; got length {key.Length}", nameof(key));

        EnsureCapacity(_count + 1);

        if (_count > 0)
        {
            ReadOnlySpan<byte> prev = _keys.AsSpan((_count - 1) * KeyLength, KeyLength);
            if (key.SequenceCompareTo(prev) <= 0)
                throw new ArgumentException($"Keys must be strictly ascending; got 0x{key[0]:X2}{key[1]:X2} after 0x{prev[0]:X2}{prev[1]:X2}", nameof(key));
        }

        long start = _writtenBeforeValue - _baseOffset;
        if ((ulong)start > (ulong)MaxDataBytes)
            throw new InvalidOperationException($"TwoByteSlotValueLarge data region exceeded {MaxDataBytes} bytes at entry {_count}");

        _starts![_count] = (uint)start;
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
            throw new InvalidOperationException($"TwoByteSlotValueLarge entry count exceeded {MaxEntries}");

        uint[] newStarts = ArrayPool<uint>.Shared.Rent(newCap);
        byte[] newKeys = ArrayPool<byte>.Shared.Rent(newCap * KeyLength);
        if (_starts is not null)
        {
            Array.Copy(_starts, newStarts, _count);
            Array.Copy(_keys!, newKeys, _count * KeyLength);
            ArrayPool<uint>.Shared.Return(_starts);
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
            throw new InvalidOperationException("TwoByteSlotValueLarge cannot encode an empty map; the caller must omit Build for zero-entry maps");

        long dataSize = _writer.Written - _baseOffset;
        if ((ulong)dataSize > (ulong)MaxDataBytes)
            throw new InvalidOperationException($"TwoByteSlotValueLarge data region {dataSize} bytes exceeds {MaxDataBytes}");

        // Offsets: N − 1 u24 LE values (Offset_1..Offset_{N-1}); Offset_0 is omitted.
        int offsetsBytes = (n - 1) * OffsetSize;
        if (offsetsBytes > 0)
        {
            Span<byte> offsetsSpan = _writer.GetSpan(offsetsBytes);
            Span<byte> scratch = stackalloc byte[4];
            for (int i = 1; i < n; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(scratch, _starts![i]);
                scratch[..OffsetSize].CopyTo(offsetsSpan[((i - 1) * OffsetSize)..]);
            }
            _writer.Advance(offsetsBytes);
        }

        // Keys: N · 2 bytes, byte-reversed on the way out (LE-stored convention; see
        // HsstTwoByteKeySearch). _keys is logical (BE) during build for the
        // strict-ascending compare in FinishValueWrite.
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
        trailer[2] = (byte)IndexType.TwoByteSlotValueLarge;
        _writer.Advance(3);
    }
}
