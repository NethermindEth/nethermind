// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Builds a <see cref="IndexType.TwoByteSlotValueLarge"/> HSST: wider sibling of
/// <see cref="HsstTwoByteSlotValueBuilder{TWriter}"/>. Same keys-first wire shape but
/// u24 LE start offsets, raising the values-section cap from 64 KiB to ~16 MiB. Keys
/// are added in strictly ascending byte order.
///
/// Output:
/// <c>[KeyCount: u16 LE = N − 1][Key_0: 2 bytes]…[Key_{N-1}: 2 bytes][Offset_1: u24 LE]…[Offset_{N-1}: u24 LE][Value_0]…[Value_{N-1}][IndexType: u8 = 0x06]</c>.
///
/// <c>Offset_0</c> is omitted (always 0); <c>Offset_N</c> (one-past-end of the values
/// section) is derived by the reader from the blob length minus the trailing
/// <see cref="IndexType"/> byte.
/// </summary>
public ref struct HsstTwoByteSlotValueLargeBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Fixed key length for this format. Single 2-byte slot suffix.</summary>
    public const int KeyLength = 2;
    /// <summary>Width on disk of each start offset (low 3 bytes of a u32, LE).</summary>
    public const int OffsetSize = 3;
    /// <summary>Maximum addressable cumulative value bytes with u24 offsets.</summary>
    public const int MaxDataBytes = (1 << 24) - 1;
    /// <summary>Maximum number of entries (<c>KeyCount</c> stores <c>N − 1</c> in a u16).</summary>
    public const int MaxEntries = 65536;

    private const int InitialCapacity = 16;
    private const int InitialValueCapacity = 256;

    private ref TWriter _writer;
    private int _count;
    private int _valueBytes;
    private uint[]? _starts;
    private byte[]? _keys;
    private byte[]? _values;

    public HsstTwoByteSlotValueLargeBuilder(ref TWriter writer)
    {
        _writer = ref writer;
        _count = 0;
        _valueBytes = 0;
    }

    public void Dispose()
    {
        if (_starts is not null) { ArrayPool<uint>.Shared.Return(_starts); _starts = null; }
        if (_keys is not null) { ArrayPool<byte>.Shared.Return(_keys); _keys = null; }
        if (_values is not null) { ArrayPool<byte>.Shared.Return(_values); _values = null; }
    }

    /// <summary>
    /// Pre-check whether a planned cumulative value size fits this format's u24 offset cap.
    /// </summary>
    public static bool FitsInOffsetWidth(long totalValueBytes)
        => (ulong)totalValueBytes <= MaxDataBytes;

    /// <summary>
    /// Append a key/value entry. <paramref name="key"/> must be exactly 2 bytes and
    /// strictly greater (byte-lex) than every previously added key. The value bytes
    /// are copied into pooled scratch and flushed to the underlying writer in
    /// <see cref="Build"/>.
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (key.Length != KeyLength)
            throw new ArgumentException($"TwoByteSlotValueLarge requires {KeyLength}-byte keys; got length {key.Length}", nameof(key));

        EnsureKeysCapacity(_count + 1);

        if (_count > 0)
        {
            ReadOnlySpan<byte> prev = _keys.AsSpan((_count - 1) * KeyLength, KeyLength);
            if (key.SequenceCompareTo(prev) <= 0)
                throw new ArgumentException($"Keys must be strictly ascending; got 0x{key[0]:X2}{key[1]:X2} after 0x{prev[0]:X2}{prev[1]:X2}", nameof(key));
        }

        long newTotal = (long)_valueBytes + value.Length;
        if ((ulong)newTotal > (ulong)MaxDataBytes)
            throw new InvalidOperationException($"TwoByteSlotValueLarge values would exceed {MaxDataBytes} bytes at entry {_count}");

        _starts![_count] = (uint)_valueBytes;
        key.CopyTo(_keys.AsSpan(_count * KeyLength, KeyLength));

        if (value.Length > 0)
        {
            EnsureValuesCapacity((int)newTotal);
            value.CopyTo(_values.AsSpan(_valueBytes, value.Length));
        }

        _valueBytes = (int)newTotal;
        _count++;
    }

    private void EnsureKeysCapacity(int needed)
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

    private void EnsureValuesCapacity(int needed)
    {
        int current = _values?.Length ?? 0;
        if (needed <= current) return;

        int newCap = current == 0 ? InitialValueCapacity : current * 2;
        if (newCap < needed) newCap = needed;

        byte[] newValues = ArrayPool<byte>.Shared.Rent(newCap);
        if (_values is not null)
        {
            Array.Copy(_values, newValues, _valueBytes);
            ArrayPool<byte>.Shared.Return(_values);
        }
        _values = newValues;
    }

    /// <summary>
    /// Emit the HSST: <c>[KeyCount][Keys][Offsets][Values][IndexType]</c>. Throws on empty
    /// maps and on values-section overflow.
    /// </summary>
    public void Build()
    {
        int n = _count;
        if (n == 0)
            throw new InvalidOperationException("TwoByteSlotValueLarge cannot encode an empty map; the caller must omit Build for zero-entry maps");

        if ((ulong)_valueBytes > (ulong)MaxDataBytes)
            throw new InvalidOperationException($"TwoByteSlotValueLarge values {_valueBytes} bytes exceeds {MaxDataBytes}");

        // Header: KeyCount (N − 1) u16 LE at byte 0.
        Span<byte> header = _writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(n - 1));
        _writer.Advance(2);

        // Keys: N · 2 bytes, byte-reversed on the way out (LE-stored).
        int keysBytes = n * KeyLength;
        Span<byte> keysSpan = _writer.GetSpan(keysBytes);
        ReadOnlySpan<byte> logicalKeys = _keys.AsSpan(0, keysBytes);
        for (int i = 0; i < n; i++)
        {
            keysSpan[i * 2 + 0] = logicalKeys[i * 2 + 1];
            keysSpan[i * 2 + 1] = logicalKeys[i * 2 + 0];
        }
        _writer.Advance(keysBytes);

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

        // Values: buffered during Add(); flush as a single contiguous block.
        if (_valueBytes > 0)
        {
            Span<byte> valuesSpan = _writer.GetSpan(_valueBytes);
            _values.AsSpan(0, _valueBytes).CopyTo(valuesSpan);
            _writer.Advance(_valueBytes);
        }

        // Trailer: single IndexType byte.
        Span<byte> trailer = _writer.GetSpan(1);
        trailer[0] = (byte)IndexType.TwoByteSlotValueLarge;
        _writer.Advance(1);
    }
}
