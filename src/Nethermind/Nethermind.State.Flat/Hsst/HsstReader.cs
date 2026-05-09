// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Non-span HSST reader generic over <typeparamref name="TReader"/>. Symmetric to
/// <see cref="HsstBTreeBuilder{TWriter}"/>: any byte source that implements
/// <see cref="IHsstByteReader"/> works — mmap, heap array, file handle, etc.
///
/// Maintains an active <see cref="Bound"/> (absolute offset+length within the reader).
/// <see cref="TrySeek"/> dispatches by <see cref="IndexType"/> into the per-layout reader
/// (<see cref="HsstBTreeReader"/>, <see cref="HsstPackedArrayReader"/>,
/// <see cref="HsstByteTagMapReader"/>) and repositions the bound to the matched entry's
/// value region, also returning that bound via <c>out matched</c>. To save/restore
/// scope across sibling seeks, capture <see cref="GetBound"/> beforehand and restore
/// with <see cref="SetBound"/>.
/// </summary>
public ref struct HsstReader<TReader, TPin>(scoped in TReader reader, Bound initialBound) : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private TReader _reader = reader;
    private Bound _bound = initialBound;

    public HsstReader(scoped in TReader reader) : this(reader, new Bound(0, reader.Length)) { }

    public readonly Bound GetBound() => _bound;
    public void SetBound(Bound bound) => _bound = bound;

    /// <summary>
    /// Copy the active bound's bytes into <paramref name="output"/>.
    /// Returns the number of bytes actually written (min of bound length and output length).
    /// </summary>
    public readonly int GetValue(Span<byte> output)
    {
        int count = (int)Math.Min(_bound.Length, output.Length);
        if (count > 0)
            _reader.TryRead(_bound.Offset, output[..count]);
        return count;
    }

    /// <summary>
    /// Exact-match B-tree lookup within the current <see cref="Bound"/>. On success sets
    /// <see cref="_bound"/> to the matched entry's value region and returns it via
    /// <paramref name="matched"/>. Returns false if no entry has exactly <paramref name="key"/>.
    /// Use <see cref="TrySeekFloor"/> for floor (largest entry ≤ key) semantics.
    /// </summary>
    public bool TrySeek(scoped ReadOnlySpan<byte> key, out Bound matched) =>
        TrySeekCore(key, exactMatch: true, out matched);

    /// <summary>
    /// Floor B-tree lookup within the current <see cref="Bound"/>. On success sets
    /// <see cref="_bound"/> to the floor entry's value region (largest stored key ≤ <paramref name="key"/>)
    /// and returns it via <paramref name="matched"/>. Returns false if the HSST is empty
    /// or <paramref name="key"/> precedes every entry.
    /// </summary>
    public bool TrySeekFloor(scoped ReadOnlySpan<byte> key, out Bound matched) =>
        TrySeekCore(key, exactMatch: false, out matched);

    private bool TrySeekCore(scoped ReadOnlySpan<byte> key, bool exactMatch, out Bound matched)
    {
        if (_bound.Length < 2) { matched = default; return false; }

        // IndexType byte is the last byte of the HSST.
        Span<byte> idxType = stackalloc byte[1];
        if (!_reader.TryRead(_bound.Offset + _bound.Length - 1, idxType)) { matched = default; return false; }
        switch ((IndexType)idxType[0])
        {
            case IndexType.BTree:
                if (HsstBTreeReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, out Bound btreeBound))
                {
                    _bound = btreeBound;
                    matched = btreeBound;
                    return true;
                }
                matched = default;
                return false;
            case IndexType.PackedArray:
                if (HsstPackedArrayReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, out Bound flatBound))
                {
                    _bound = flatBound;
                    matched = flatBound;
                    return true;
                }
                matched = default;
                return false;
            case IndexType.ByteTagMap:
                if (HsstByteTagMapReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, out Bound tagBound))
                {
                    _bound = tagBound;
                    matched = tagBound;
                    return true;
                }
                matched = default;
                return false;
            case IndexType.DenseByteIndex:
                if (HsstDenseByteIndexReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, out Bound denseBound))
                {
                    _bound = denseBound;
                    matched = denseBound;
                    return true;
                }
                matched = default;
                return false;
            default:
                matched = default;
                return false;
        }
    }

    public void Dispose()
    {
        // No owned resources; pins are released per-iteration in the per-layout readers.
    }
}
