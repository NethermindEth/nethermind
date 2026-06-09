// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.TwoByteSlot;

/// <summary>
/// Builds a keys-first TwoByteSlot value HSST: fixed 2-byte keys, variable values, packed
/// start-offset section. The wire shape lets the reader prefetch keys/offsets ahead of the
/// bulk values. The on-disk offset width is selected per build via <c>offsetSize</c>:
/// <c>2</c> emits <see cref="IndexType.TwoByteSlotValue"/> (u16 offsets, values capped at
/// <c>ushort.MaxValue</c>); <c>3</c> emits <see cref="IndexType.TwoByteSlotValueLarge"/>
/// (u24 offsets, ~16 MiB cap).
///
/// Output:
/// <c>[IndexType: u8][KeyCount: u16 LE = N − 1][Key_0: 2 bytes]…[Key_{N-1}: 2 bytes][Offset_1: offsetSize LE]…[Offset_{N-1}: offsetSize LE][Value_0]…[Value_{N-1}]</c>.
///
/// The <see cref="IndexType"/> byte leads the blob (not a trailer) so a reader that already
/// knows it is descending into a keys-first sub-slot dispatches on byte 0 and then reads
/// <c>KeyCount</c>, keys and offsets in the same forward pass — no tail seek.
///
/// <c>Offset_i</c> is the exclusive start offset of <c>Value_i</c> measured from the start of
/// the values section (= byte after the offsets array). <c>Offset_0</c> is omitted because it
/// is always 0; <c>Offset_N</c> (one-past-end of the values section) is derived by the reader
/// as the blob's end. Hence per-entry value bounds are <c>[Offset_i, Offset_{i+1})</c>.
///
/// <see cref="Build"/> throws when the cumulative value bytes exceed the chosen width's cap;
/// the caller is expected to gate on <see cref="FitsInOffsetWidth"/> to pick <c>offsetSize</c>.
/// Values must be known up-front because the offset section is emitted ahead of them: the
/// builder buffers value bytes into pooled scratch during <see cref="Add"/> and flushes them
/// in <see cref="Build"/>.
/// </summary>
public ref struct HsstTwoByteSlotValueBuilder<TWriter>
    where TWriter : IByteBufferWriter
{
    /// <summary>Fixed key length for this format. Single 2-byte slot suffix.</summary>
    public const int KeyLength = 2;
    /// <summary>Maximum number of entries (<c>KeyCount</c> stores <c>N − 1</c> in a u16).</summary>
    public const int MaxEntries = 65536;

    private const int InitialCapacity = 16;
    private const int InitialValueCapacity = 256;

    private ref TWriter _writer;
    private readonly int _offsetSize;
    private readonly int _maxDataBytes;
    private int _count;
    private int _valueBytes;
    private uint[]? _starts;
    private byte[]? _keys;
    private byte[]? _values;

    /// <param name="writer">Destination writer; receives one TwoByteSlot value HSST blob.</param>
    /// <param name="offsetSize">On-disk offset width: <c>2</c> (u16, <see cref="IndexType.TwoByteSlotValue"/>,
    /// caps values at 64 KiB) or <c>3</c> (u24, <see cref="IndexType.TwoByteSlotValueLarge"/>, ~16 MiB).</param>
    public HsstTwoByteSlotValueBuilder(ref TWriter writer, int offsetSize = 2)
    {
        _writer = ref writer;
        _offsetSize = offsetSize;
        _maxDataBytes = (1 << (8 * offsetSize)) - 1;
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
    /// Pre-check whether a planned cumulative value size fits the narrow (u16) offset width.
    /// Callers gate on this to choose between the default 2-byte offsets and the wider
    /// 3-byte (<c>offsetSize: 3</c>) form.
    /// </summary>
    public static bool FitsInOffsetWidth(long totalValueBytes)
        => (ulong)totalValueBytes <= ushort.MaxValue;

    /// <summary>
    /// Append a key/value entry. <paramref name="key"/> must be exactly 2 bytes and
    /// strictly greater (byte-lex) than every previously added key. The value bytes
    /// are copied into pooled scratch and flushed to the underlying writer in
    /// <see cref="Build"/>; callers may reuse the source span after the call returns.
    /// </summary>
    public void Add(scoped ReadOnlySpan<byte> key, scoped ReadOnlySpan<byte> value)
    {
        if (key.Length != KeyLength)
            throw new ArgumentException($"TwoByteSlotValue requires {KeyLength}-byte keys; got length {key.Length}", nameof(key));

        EnsureKeysCapacity(_count + 1);

        if (_count > 0)
        {
            ReadOnlySpan<byte> prev = _keys.AsSpan((_count - 1) * KeyLength, KeyLength);
            if (key.SequenceCompareTo(prev) <= 0)
                throw new ArgumentException($"Keys must be strictly ascending; got 0x{key[0]:X2}{key[1]:X2} after 0x{prev[0]:X2}{prev[1]:X2}", nameof(key));
        }

        long newTotal = (long)_valueBytes + value.Length;
        if ((ulong)newTotal > (ulong)_maxDataBytes)
            throw new InvalidOperationException($"TwoByteSlotValue values would exceed {_maxDataBytes} bytes at entry {_count}");

        _starts![_count] = (uint)_valueBytes;
        key.CopyTo(_keys.AsSpan(_count * KeyLength, KeyLength));

        if (value.Length > 0)
        {
            EnsureValuesCapacity(_valueBytes + value.Length);
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
            throw new InvalidOperationException($"TwoByteSlotValue entry count exceeded {MaxEntries}");

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
    /// Emit the HSST: <c>[IndexType][KeyCount][Keys][Offsets][Values]</c>. Throws on empty
    /// maps and on values-section overflow.
    /// </summary>
    public void Build()
    {
        int n = _count;
        if (n == 0)
            throw new InvalidOperationException("TwoByteSlotValue cannot encode an empty map; the caller must omit Build for zero-entry maps");

        if ((ulong)_valueBytes > (ulong)_maxDataBytes)
            throw new InvalidOperationException($"TwoByteSlotValue values {_valueBytes} bytes exceeds {_maxDataBytes}");

        // IndexType byte at byte 0 — leads the blob so a nested-slot reader dispatches
        // on the first byte and reads the rest of the metadata forward without a tail seek.
        Span<byte> indexType = _writer.GetSpan(1);
        indexType[0] = (byte)(_offsetSize == KeyLength ? IndexType.TwoByteSlotValue : IndexType.TwoByteSlotValueLarge);
        _writer.Advance(1);

        // Header: KeyCount (N − 1) u16 LE.
        Span<byte> header = _writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(n - 1));
        _writer.Advance(2);

        // Keys: N · 2 bytes, byte-reversed on the way out (LE-stored convention — a native
        // u16 load over a stored key now recovers the BE numeric value, letting SIMD
        // scans compare numerically; see UniformKeySearch.LowerBound2LE). _keys is logical
        // (BE) during build for the strict-ascending compare in Add().
        int keysBytes = n * KeyLength;
        Span<byte> keysSpan = _writer.GetSpan(keysBytes);
        HsstTwoByteSlotKeys.CopyLogicalToStored(_keys.AsSpan(0, keysBytes), keysSpan);
        _writer.Advance(keysBytes);

        // Offsets: N − 1 LE values of width offsetSize (Offset_1..Offset_{N-1}); Offset_0 is omitted.
        int offsetsBytes = (n - 1) * _offsetSize;
        if (offsetsBytes > 0)
        {
            Span<byte> offsetsSpan = _writer.GetSpan(offsetsBytes);
            Span<byte> scratch = stackalloc byte[4];
            for (int i = 1; i < n; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(scratch, _starts![i]);
                scratch[.._offsetSize].CopyTo(offsetsSpan[((i - 1) * _offsetSize)..]);
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
    }
}
