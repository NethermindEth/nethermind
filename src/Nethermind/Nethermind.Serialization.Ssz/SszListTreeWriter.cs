// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

/// <summary>Writes the tree of one encoded SSZ item.</summary>
public delegate void SszItemTreeBuilder(SszTreeWriter output, ReadOnlySpan<byte> item);

/// <summary>Opens the writer of the tree of an item, which mirrors <paramref name="source"/> if it exists.</summary>
public delegate T SszItemTreeWriter<T>(SszTreeWriter output, SszTreeReader source) where T : allows ref struct;

/// <summary>How the items of a collection become the leaves of its tree.</summary>
/// <remarks>With neither <paramref name="hash"/> nor <paramref name="build"/>, the encoded items are the leaves.</remarks>
/// <param name="shape">The layout of the tree of the collection.</param>
/// <param name="itemLength">The encoded length of every item, or 0 when items are variable-size.</param>
/// <param name="isList">Whether the length is mixed in.</param>
/// <param name="hash">Hashes an item that has no tree.</param>
/// <param name="build">Writes the tree of an item.</param>
/// <param name="itemShape">The layout of the tree of an item.</param>
public readonly struct SszTreeItems(SszTreeShape shape, int itemLength, bool isList, SszHasher? hash, SszItemTreeBuilder? build, SszTreeShape itemShape)
{
    /// <summary>The layout of the tree of the collection.</summary>
    public SszTreeShape Shape => shape;

    internal int ItemLength => itemLength;
    internal bool IsList => isList;
    internal SszHasher? Hash => hash;
    internal SszItemTreeBuilder? Build => build;
    internal SszTreeShape? ItemShape => build is null ? null : itemShape;

    /// <summary>Whether leaves are kept until the tree ends, because trees of the items or of their members are written first.</summary>
    internal bool IsBuffered => build is not null || itemLength == 0;
}

/// <summary>Writes the tree of a collection.</summary>
/// <remarks><c>default</c> stands for no tree, and ignores what is written to it.</remarks>
public readonly ref struct SszListTreeWriter
{
    private readonly SszTreeWriter? _output;
    private readonly SszTreeItems _items;
    private readonly SszTreeReader _source;
    private readonly SszHasher? _hashCollection;
    private readonly Span<byte> _leafCount;
    private readonly int _id;

    /// <param name="output">Receives the tree.</param>
    /// <param name="items">How items become leaves.</param>
    /// <param name="source">The tree to copy unchanged nodes from, <c>default</c> when there is none.</param>
    public SszListTreeWriter(SszTreeWriter output, SszTreeItems items, SszTreeReader source)
    {
        _output = output;
        _items = items;
        _source = source;
        _id = items.IsBuffered ? output.OpenBuffered() : output.OpenStreaming(items.Shape, out _leafCount);
    }

    /// <summary>Stands for a collection that has no tree in a container that has one, so only its hash is needed.</summary>
    /// <param name="output">Receives the hash.</param>
    /// <param name="hashCollection">Hashes the whole collection.</param>
    public SszListTreeWriter(SszTreeWriter output, SszHasher hashCollection)
    {
        _output = output;
        _hashCollection = hashCollection;
    }

    private bool HasTree => _output is not null && _hashCollection is null;

    /// <summary>Adds encoded items: any number that are next to each other when they are fixed-size, otherwise one.</summary>
    public void Add(ReadOnlySpan<byte> items)
    {
        if (!HasTree) return;

        if (_items is { IsBuffered: false, Hash: null })
        {
            _output!.AddPacked(_id, items.Length / _items.ItemLength, items, _source);
            return;
        }

        if (_items.ItemLength == 0)
        {
            AddItem(items);
            return;
        }

        for (; !items.IsEmpty; items = items[_items.ItemLength..]) AddItem(items[.._items.ItemLength]);
    }

    /// <summary>Adds all items of an encoded collection.</summary>
    public void AddAll(ReadOnlySpan<byte> collection)
    {
        if (_items.ItemLength != 0)
        {
            Add(collection);
            return;
        }

        foreach (ReadOnlySpan<byte> item in Items(collection)) Add(item);
    }

    /// <summary>Adds <paramref name="count"/> items of the source, from the one at <paramref name="start"/>.</summary>
    /// <param name="start">The first item.</param>
    /// <param name="count">The number of items.</param>
    /// <param name="collection">The encoded source, which is hashed when it has no tree.</param>
    /// <exception cref="ArgumentOutOfRangeException">The source does not have these items.</exception>
    public void Copy(int start, int count, ReadOnlySpan<byte> collection)
    {
        if (!HasTree || count == 0) return;

        if (_items.ItemLength == 0)
        {
            SszListReader<ReadOnlySpan<byte>> items = Items(collection);
            ArgumentOutOfRangeException.ThrowIfNegative(start);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, items.Count - start);
            if (_source.Exists) _output!.CopyItems(_id, _source, start, count, _items.ItemShape);
            else for (int i = start; i < start + count; i++) AddItem(items[i]);
            return;
        }

        ReadOnlySpan<byte> encoded = collection.Slice(checked(start * _items.ItemLength), checked(count * _items.ItemLength));
        if (!_source.Exists) Add(encoded);
        else if (_items.IsBuffered) _output!.CopyItems(_id, _source, start, count, _items.ItemShape);
        else if (_items.Hash is not null) _output!.CopyLeaves(_id, _source, start, count);
        else _output!.CopyPacked(_id, count, (long)start * _items.ItemLength, encoded, _source);
    }

    /// <summary>Adds all items of the source.</summary>
    public void CopyAll(ReadOnlySpan<byte> collection)
    {
        if (HasTree) Copy(0, _items.ItemLength == 0 ? Items(collection).Count : collection.Length / _items.ItemLength, collection);
    }

    /// <summary>Ends the tree.</summary>
    public void Dispose()
    {
        if (!HasTree) return;

        if (_items.IsBuffered) _output!.CloseBuffered(_id, _items.IsList, _items.Shape, _source);
        else _output!.CloseStreaming(_id, _items.IsList, _leafCount, _source);
    }

    /// <summary>The output that the writer of an item reports to, or <c>null</c> when items have no hash of their own.</summary>
    internal SszTreeWriter? ItemOutput => HasTree ? _output : null;

    /// <summary>The tree of the item of the source at <paramref name="index"/>, if it has one.</summary>
    internal SszTreeReader SourceItem(int index) =>
        _source.Exists && _items.ItemShape is { } shape ? _source.Inner(index, shape, shape) : default;

    /// <summary>Begins an item whose writer reports its hash.</summary>
    internal void BeginItem()
    {
        if (HasTree) _output!.BeginItem(_id);
    }

    /// <summary>Ends the collection that was encoded from <paramref name="start"/>.</summary>
    internal void Close(SszWriter writer, int start)
    {
        if (_hashCollection is null)
        {
            Dispose();
            return;
        }

        writer.Hash(start, _hashCollection, out UInt256 root);
        _output!.EndMember(in root);
    }

    /// <summary>Ends the collection that was encoded in <paramref name="encoded"/>.</summary>
    internal void Close(ReadOnlySpan<byte> encoded)
    {
        if (_hashCollection is null)
        {
            Dispose();
            return;
        }

        _hashCollection(encoded, out UInt256 root);
        _output!.EndMember(in root);
    }

    private static SszListReader<ReadOnlySpan<byte>> Items(ReadOnlySpan<byte> collection) => new(collection, 0, static item => item);

    private void AddItem(ReadOnlySpan<byte> item)
    {
        if (_items.Build is { } build)
        {
            _output!.BeginItem(_id);
            build(_output, item);
            return;
        }

        _items.Hash!(item, out UInt256 root);
        if (_items.IsBuffered)
        {
            _output!.BeginItem(_id);
            _output.EndMember(in root);
        }
        else
        {
            _output!.AddLeaf(_id, in root, _source);
        }
    }
}

/// <summary>Writes the tree of a collection whose every item has a tree.</summary>
/// <typeparam name="T">Writes the tree of an item.</typeparam>
public readonly ref struct SszVariableListTreeWriter<T>(SszTreeWriter output, SszTreeItems items, SszTreeReader source, SszItemTreeWriter<T> beginItem)
    where T : allows ref struct
{
    private readonly SszListTreeWriter _list = new(output, items, source);

    /// <summary>The writer without the type of the items.</summary>
    public SszListTreeWriter List => _list;

    /// <summary>Opens the writer of the next item, which has to be disposed before this writer is used again.</summary>
    public T BeginItem()
    {
        _list.BeginItem();
        return beginItem(output, default);
    }

    /// <inheritdoc cref="BeginItem()"/>
    /// <param name="sourceIndex">The item of the source that the writer mirrors.</param>
    public T BeginItem(int sourceIndex)
    {
        SszTreeReader item = _list.SourceItem(sourceIndex);
        _list.BeginItem();
        return beginItem(output, item);
    }

    /// <inheritdoc cref="SszListTreeWriter.Copy"/>
    public void Copy(int start, int count, ReadOnlySpan<byte> collection) => _list.Copy(start, count, collection);

    /// <summary>Ends the tree.</summary>
    public void Dispose() => _list.Dispose();
}
