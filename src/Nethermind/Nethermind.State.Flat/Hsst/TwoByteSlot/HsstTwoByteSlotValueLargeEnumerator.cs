// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.TwoByteSlot;

/// <summary>
/// TwoByteSlotValueLarge cursor for <see cref="HsstEnumerator{TReader,TPin}"/>: the
/// u24-offset sibling of <see cref="HsstTwoByteSlotValueEnumerator{TReader,TPin}"/>.
/// Same iteration shape but reads u24 (3-byte LE) start offsets instead of u16.
/// Heap-allocated so the dispatcher struct can be value-copied without losing
/// iteration state.
/// </summary>
internal sealed class HsstTwoByteSlotValueLargeEnumerator<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private readonly HsstTwoByteSlotValueLargeReader.Layout _layout;
    private int _index = -1;
    private long _currentValueStart;
    private long _currentValueEnd;

    public static HsstTwoByteSlotValueLargeEnumerator<TReader, TPin>? TryCreate(scoped in TReader reader, Bound scope)
    {
        if (!HsstTwoByteSlotValueLargeReader.TryReadLayout<TReader, TPin>(in reader, scope, out HsstTwoByteSlotValueLargeReader.Layout layout))
            return null;
        return new HsstTwoByteSlotValueLargeEnumerator<TReader, TPin>(layout);
    }

    private HsstTwoByteSlotValueLargeEnumerator(HsstTwoByteSlotValueLargeReader.Layout layout) => _layout = layout;

    public long Count => _layout.Count;

    public bool MoveNext(scoped in TReader reader)
    {
        int next = _index + 1;
        if (next >= _layout.Count) return false;
        _index = next;
        long start = _index == 0 ? 0L : HsstTwoByteSlotValueLargeReader.ReadU24LE<TReader, TPin>(in reader, _layout.OffsetsStart + (long)(_index - 1) * HsstTwoByteSlotValueLargeReader.OffsetSize);
        long end = _index == _layout.Count - 1
            ? _layout.ValuesEnd - _layout.ValuesStart
            : HsstTwoByteSlotValueLargeReader.ReadU24LE<TReader, TPin>(in reader, _layout.OffsetsStart + (long)_index * HsstTwoByteSlotValueLargeReader.OffsetSize);
        _currentValueStart = _layout.ValuesStart + start;
        _currentValueEnd = _layout.ValuesStart + end;
        return true;
    }

    public Bound CurrentKey => new(_layout.KeysStart + (long)_index * HsstTwoByteSlotValueLargeReader.KeyLength, HsstTwoByteSlotValueLargeReader.KeyLength);
    public Bound CurrentValue => new(_currentValueStart, _currentValueEnd - _currentValueStart);
    public long CurrentMetadataStart => _currentValueEnd;
}
