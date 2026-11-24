// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;

namespace Nethermind.Serialization.Ssz;

/// <summary>Encodes fixed-size SSZ items one after another.</summary>
public delegate void SszItemEncoder<T>(Span<byte> span, ReadOnlySpan<T> items);

/// <summary>Opens the writer of one variable-size SSZ item, which mirrors <paramref name="source"/> if <paramref name="hasSource"/>.</summary>
/// <param name="writer">Receives the bytes.</param>
/// <param name="source">The encoded item to mirror.</param>
/// <param name="hasSource">Whether there is an item to mirror.</param>
/// <param name="trees">Receives the hash of the item, or <c>null</c> when it is not needed.</param>
/// <param name="sourceTree">The tree of the item to mirror, if it has one.</param>
public delegate T SszItemWriter<T>(SszWriter writer, ReadOnlySpan<byte> source, bool hasSource, SszTreeWriter? trees, SszTreeReader sourceTree) where T : allows ref struct;

/// <summary>Writes the items of an SSZ list of fixed-size items.</summary>
/// <param name="writer">Receives the bytes.</param>
/// <param name="itemLength">The encoded length of every item.</param>
/// <param name="limit">The most items that the list may have.</param>
/// <param name="encode">Encodes items.</param>
/// <param name="source">The encoded list to copy unchanged items from, empty when there is none.</param>
/// <param name="tree">Receives the same items.</param>
public readonly ref struct SszListWriter<T>(SszWriter writer, int itemLength, ulong limit, SszItemEncoder<T> encode, ReadOnlySpan<byte> source, SszListTreeWriter tree)
{
    private readonly ReadOnlySpan<byte> _source = source;
    private readonly SszListTreeWriter _tree = tree;
    private readonly int _start = writer.Length;
    private readonly int _id = writer.Open();

    /// <inheritdoc cref="Add(ReadOnlySpan{T})"/>
    public void Add(T item) => Add(new ReadOnlySpan<T>(in item));

    /// <summary>Appends to the list.</summary>
    /// <exception cref="InvalidDataException">The list would exceed its limit.</exception>
    /// <exception cref="InvalidOperationException">This writer is closed.</exception>
    public void Add(scoped ReadOnlySpan<T> items)
    {
        AddItems(items.Length);
        Span<byte> encoded = writer.Reserve(checked(items.Length * itemLength));
        encode(encoded, items);
        _tree.Add(encoded);
    }

    /// <summary>Appends <paramref name="count"/> unchanged items of the source, from the item at <paramref name="start"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The source does not have these items.</exception>
    /// <inheritdoc cref="Add(ReadOnlySpan{T})"/>
    public void Copy(int start, int count)
    {
        ReadOnlySpan<byte> items = _source.Slice(checked(start * itemLength), checked(count * itemLength));
        AddItems(count);
        writer.Write(items);
        _tree.Copy(start, count, _source);
    }

    /// <summary>Ends the list.</summary>
    public void Dispose()
    {
        if (writer.Close(_id)) _tree.Close(writer, _start);
    }

    private void AddItems(int count)
    {
        int total = writer.AddItems(_id, count);
        if ((ulong)total > limit)
        {
            throw new InvalidDataException($"Invalid SSZ list: expected at most {limit} items but found {total}.");
        }
    }
}

/// <summary>Writes the items of an SSZ vector of fixed-size items.</summary>
/// <param name="writer">Tracks the items that are written.</param>
/// <param name="destination">Where the vector is encoded.</param>
/// <param name="itemLength">The encoded length of every item.</param>
/// <param name="encode">Encodes items.</param>
/// <param name="source">The encoded vector to copy unchanged items from, empty when there is none.</param>
/// <param name="tree">Receives the same items.</param>
public readonly ref struct SszVectorWriter<T>(SszWriter writer, Span<byte> destination, int itemLength, SszItemEncoder<T> encode, ReadOnlySpan<byte> source, SszListTreeWriter tree)
{
    private readonly Span<byte> _destination = destination;
    private readonly ReadOnlySpan<byte> _source = source;
    private readonly SszListTreeWriter _tree = tree;
    private readonly int _id = writer.Open();

    /// <inheritdoc cref="Add(ReadOnlySpan{T})"/>
    public void Add(T item) => Add(new ReadOnlySpan<T>(in item));

    /// <summary>Writes the next items of the vector.</summary>
    /// <exception cref="InvalidDataException">The vector would exceed its length.</exception>
    /// <exception cref="InvalidOperationException">This writer is closed.</exception>
    public void Add(scoped ReadOnlySpan<T> items)
    {
        Span<byte> encoded = AddItems(items.Length);
        encode(encoded, items);
        _tree.Add(encoded);
    }

    /// <summary>Writes <paramref name="count"/> unchanged items of the source, from the item at <paramref name="start"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The source does not have these items.</exception>
    /// <inheritdoc cref="Add(ReadOnlySpan{T})"/>
    public void Copy(int start, int count)
    {
        ReadOnlySpan<byte> items = _source.Slice(checked(start * itemLength), checked(count * itemLength));
        items.CopyTo(AddItems(count));
        _tree.Copy(start, count, _source);
    }

    /// <summary>Ends the vector if all of its items are written.</summary>
    public void Dispose()
    {
        if (writer.Close(_id, _destination.Length / itemLength)) _tree.Close(_destination);
    }

    private Span<byte> AddItems(int count)
    {
        int total = writer.AddItems(_id, count);
        int length = _destination.Length / itemLength;
        if (total > length)
        {
            throw new InvalidDataException($"Invalid SSZ vector: expected {length} items but found {total}.");
        }

        return _destination.Slice((total - count) * itemLength, count * itemLength);
    }
}

/// <summary>Writes the items of an SSZ vector or list of variable-size items.</summary>
/// <typeparam name="TWriter">The writer of an item.</typeparam>
/// <param name="writer">Receives the bytes.</param>
/// <param name="count">The number of items, which the offset table that precedes them depends on.</param>
/// <param name="beginItem">Opens the writer of an item.</param>
/// <param name="source">The encoded collection to copy unchanged items from, empty when there is none.</param>
/// <param name="tree">Receives the same items.</param>
public readonly ref struct SszVariableListWriter<TWriter>(SszWriter writer, int count, SszItemWriter<TWriter> beginItem, ReadOnlySpan<byte> source, SszListTreeWriter tree)
    where TWriter : allows ref struct
{
    private const int OffsetSize = sizeof(uint);

    private readonly ReadOnlySpan<byte> _sourceBytes = source;
    private readonly SszListReader<ReadOnlySpan<byte>> _source = new(source, 0, static item => item);
    private readonly SszListTreeWriter _tree = tree;
    private readonly int _start = writer.Length;
    private readonly int _id = writer.Open();
    private readonly Span<byte> _offsets = writer.Reserve(checked(count * OffsetSize));

    /// <summary>Opens the writer of the next item, which has to be disposed before this writer is used again.</summary>
    /// <exception cref="InvalidOperationException">All items are written, or the writer of the previous item is open.</exception>
    public TWriter BeginItem()
    {
        AddItem(writer.Length);
        _tree.BeginItem();
        return beginItem(writer, default, false, _tree.ItemOutput, default);
    }

    /// <inheritdoc cref="BeginItem()"/>
    /// <param name="sourceIndex">The item of the source that the writer mirrors.</param>
    /// <exception cref="ArgumentOutOfRangeException">The source does not have this item.</exception>
    public TWriter BeginItem(int sourceIndex)
    {
        ReadOnlySpan<byte> item = _source[sourceIndex];
        AddItem(writer.Length);
        _tree.BeginItem();
        return beginItem(writer, item, true, _tree.ItemOutput, _tree.SourceItem(sourceIndex));
    }

    /// <summary>Writes <paramref name="itemCount"/> unchanged items of the source, from the item at <paramref name="start"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The source does not have these items.</exception>
    /// <exception cref="InvalidOperationException">All items are written, or the writer of the previous item is open.</exception>
    public void Copy(int start, int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        if (itemCount == 0) return;

        int position = writer.Length;
        for (int i = 0; i < itemCount; i++)
        {
            int length = _source[start + i].Length;
            AddItem(position);
            position += length;
        }

        // Each item ends where the next one starts, so the items are contiguous in the source too.
        writer.Write(_source.Slice(start, itemCount));
        _tree.Copy(start, itemCount, _sourceBytes);
    }

    /// <summary>Ends the collection if all of its items are written.</summary>
    public void Dispose()
    {
        if (writer.Close(_id, count)) _tree.Close(writer, _start);
    }

    private void AddItem(int position)
    {
        int index = writer.AddItems(_id, 1) - 1;
        if (index >= count)
        {
            throw new InvalidOperationException($"SSZ writer: the list has {count} items.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(_offsets.Slice(index * OffsetSize, OffsetSize), position - _start);
    }
}
