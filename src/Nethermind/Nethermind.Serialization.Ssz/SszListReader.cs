// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

/// <summary>Merkleizes one encoded SSZ value.</summary>
public delegate void SszHasher(ReadOnlySpan<byte> encoded, out UInt256 root);

/// <summary>Reads one SSZ collection item from its encoded bytes.</summary>
public delegate T SszItemReader<T>(ReadOnlySpan<byte> item) where T : allows ref struct;

/// <summary>Reads the items of an encoded SSZ vector or list on access, without decoding the collection.</summary>
/// <remarks>
/// The constructor checks only the item count. For variable-size items, the indexer checks the offsets of the requested
/// item and not the whole offset table, so reading one item does not prove that the collection would decode.
/// </remarks>
public readonly ref struct SszListReader<T> where T : allows ref struct
{
    private const int OffsetSize = sizeof(uint);

    private readonly ReadOnlySpan<byte> _data;
    private readonly int _itemLength;
    private readonly SszItemReader<T> _readItem;

    /// <param name="data">The encoded collection.</param>
    /// <param name="itemLength">The encoded length of every item, or 0 when items are variable-size.</param>
    /// <param name="readItem">Reads an item from its bytes.</param>
    /// <exception cref="InvalidDataException"><paramref name="data"/> does not hold a whole number of items.</exception>
    public SszListReader(ReadOnlySpan<byte> data, int itemLength, SszItemReader<T> readItem)
    {
        _data = data;
        _itemLength = itemLength;
        _readItem = readItem;

        if (data.IsEmpty)
        {
            return;
        }

        if (itemLength != 0)
        {
            (Count, int remainder) = Math.DivRem(data.Length, itemLength);
            if (remainder != 0)
            {
                throw new InvalidDataException($"Invalid SSZ collection: {data.Length} bytes is not a multiple of the item length {itemLength}.");
            }
        }
        else
        {
            int firstOffset = data.Length < OffsetSize ? 0 : ReadOffset(0);
            if (firstOffset < OffsetSize || firstOffset > data.Length || firstOffset % OffsetSize != 0)
            {
                throw new InvalidDataException($"Invalid SSZ collection: the first offset {firstOffset} does not fit {data.Length} bytes.");
            }

            Count = firstOffset / OffsetSize;
        }
    }

    /// <summary>The number of items.</summary>
    public int Count { get; }

    /// <summary>Reads the item at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="Count"/>.</exception>
    /// <exception cref="InvalidDataException">The item's offsets are out of order or out of bounds, or its bytes are invalid.</exception>
    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _readItem(Slice(index, 1));
        }
    }

    public Enumerator GetEnumerator() => new(this);

    /// <summary>The bytes of <paramref name="count"/> consecutive items, at least one, which the caller has checked to exist.</summary>
    internal ReadOnlySpan<byte> Slice(int index, int count)
    {
        if (_itemLength != 0)
        {
            return _data.Slice(index * _itemLength, count * _itemLength);
        }

        int start = ReadOffset(index);
        int end = index + count == Count ? _data.Length : ReadOffset(index + count);
        return start >= Count * OffsetSize && start <= end && end <= _data.Length
            ? _data[start..end]
            : throw new InvalidDataException($"Invalid SSZ collection: item {index} spans [{start}, {end}) of {_data.Length} bytes.");
    }

    private int ReadOffset(int index) => BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(index * OffsetSize, OffsetSize));

    public ref struct Enumerator(SszListReader<T> items)
    {
        private readonly SszListReader<T> _items = items;
        private int _index = -1;

        public readonly T Current => _items[_index];

        public bool MoveNext() => ++_index < _items.Count;
    }
}
