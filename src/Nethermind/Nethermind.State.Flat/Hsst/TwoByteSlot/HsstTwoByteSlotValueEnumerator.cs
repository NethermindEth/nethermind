// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.TwoByteSlot;

/// <summary>
/// TwoByteSlotValue cursor for <see cref="HsstEnumerator{TReader,TPin}"/>: fixed 2-byte
/// keys, variable values, keys-first wire shape with the offsets section between keys
/// and values. Forward iteration is a flat index walk; bounds derive from a single u16
/// offset read per entry (or zero / values-end for the endpoints). Heap-allocated so
/// the dispatcher struct can be value-copied without losing iteration state.
/// </summary>
internal sealed class HsstTwoByteSlotValueEnumerator<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private readonly HsstTwoByteSlotValueReader.Layout _layout;
    private int _index = -1;
    private long _currentValueStart;
    private long _currentValueEnd;

    public static HsstTwoByteSlotValueEnumerator<TReader, TPin>? TryCreate(scoped in TReader reader, Bound scope)
    {
        if (!HsstTwoByteSlotValueReader.TryReadLayout<TReader, TPin>(in reader, scope, out HsstTwoByteSlotValueReader.Layout layout))
            return null;
        return new HsstTwoByteSlotValueEnumerator<TReader, TPin>(layout);
    }

    private HsstTwoByteSlotValueEnumerator(HsstTwoByteSlotValueReader.Layout layout) => _layout = layout;

    public long Count => _layout.Count;

    public bool MoveNext(scoped in TReader reader)
    {
        int next = _index + 1;
        if (next >= _layout.Count) return false;
        _index = next;
        // Start of this entry: 0 if first, else Offset_{index} stored at offsetsStart + 2*(index-1).
        long start = _index == 0 ? 0L : ReadU16LE(in reader, _layout.OffsetsStart + (long)(_index - 1) * 2);
        // End of this entry: values-section end if last, else Offset_{index+1} stored at offsetsStart + 2*index.
        long end = _index == _layout.Count - 1
            ? _layout.ValuesEnd - _layout.ValuesStart
            : ReadU16LE(in reader, _layout.OffsetsStart + (long)_index * 2);
        _currentValueStart = _layout.ValuesStart + start;
        _currentValueEnd = _layout.ValuesStart + end;
        return true;
    }

    public Bound CurrentKey => new(_layout.KeysStart + (long)_index * HsstTwoByteSlotValueReader.KeyLength, HsstTwoByteSlotValueReader.KeyLength);
    public Bound CurrentValue => new(_currentValueStart, _currentValueEnd - _currentValueStart);
    public long CurrentMetadataStart => _currentValueEnd;

    private static long ReadU16LE(scoped in TReader reader, long offset)
    {
        Span<byte> buf = stackalloc byte[2];
        reader.TryRead(offset, buf);
        return BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }
}
