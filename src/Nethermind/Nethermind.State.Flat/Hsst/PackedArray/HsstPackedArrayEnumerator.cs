// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Hsst.PackedArray;

/// <summary>
/// PackedArray cursor for <see cref="HsstEnumerator{TReader,TPin}"/>: fixed key/value
/// stride, no offset table — entry positions are computed on the fly. Heap-allocated
/// so the dispatcher struct can be value-copied without losing iteration state.
/// </summary>
internal sealed class HsstPackedArrayEnumerator<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private readonly long _dataStart;
    private readonly int _keySize;
    private readonly int _valueSize;
    private readonly int _stride;
    private readonly long _count;
    private readonly bool _isLittleEndian;
    private long _index = -1;
    private long _currentEntryStart;

    public static HsstPackedArrayEnumerator<TReader, TPin>? TryCreate(scoped in TReader reader, Bound scope)
    {
        if (!HsstPackedArrayReader.TryReadLayout<TReader, TPin>(in reader, scope, out HsstPackedArrayReader.Layout layout))
        {
            return null;
        }
        return new HsstPackedArrayEnumerator<TReader, TPin>(layout);
    }

    private HsstPackedArrayEnumerator(HsstPackedArrayReader.Layout layout)
    {
        _dataStart = layout.DataStart;
        _keySize = layout.KeySize;
        _valueSize = layout.ValueSize;
        _stride = layout.EntryStride;
        _count = layout.EntryCount;
        _isLittleEndian = layout.IsLittleEndian;
    }

    public long Count => _count;
    public bool IsLittleEndian => _isLittleEndian;

    public bool MoveNext()
    {
        if (++_index >= _count) return false;
        _currentEntryStart = _dataStart + _index * _stride;
        return true;
    }

    public Bound CurrentKey => new(_currentEntryStart, _keySize);
    public Bound CurrentValue => new(_currentEntryStart + _keySize, _valueSize);
    public long CurrentMetadataStart => _currentEntryStart + _keySize;
}
