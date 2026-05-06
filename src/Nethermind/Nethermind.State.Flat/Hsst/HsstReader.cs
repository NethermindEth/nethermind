// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Non-span HSST reader generic over <typeparamref name="TReader"/>. Symmetric to
/// <see cref="HsstBuilder{TWriter}"/>: any byte source that implements
/// <see cref="IHsstByteReader"/> works — mmap, heap array, file handle, etc.
///
/// Maintains an active <see cref="Bound"/> (absolute offset+length within the reader).
/// <see cref="TrySeek"/> dispatches by <see cref="IndexType"/> into the per-layout reader
/// (<see cref="HsstBTreeReader"/>, <see cref="HsstPackedArrayReader"/>,
/// <see cref="HsstByteTagMapReader"/>) and repositions the bound to the matched entry's
/// value region; the caller saves/restores scope via <see cref="GetBound"/> /
/// <see cref="SetBound"/> using the <c>out previousBound</c> parameter.
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
    /// <see cref="_bound"/> to the matched entry's value region and returns the prior bound via
    /// <paramref name="previousBound"/>. Returns false if no entry has exactly <paramref name="key"/>.
    /// Use <see cref="TrySeekFloor"/> for floor (largest entry ≤ key) semantics.
    /// </summary>
    public bool TrySeek(scoped ReadOnlySpan<byte> key, out Bound previousBound) =>
        TrySeekCore(key, exactMatch: true, out previousBound);

    /// <summary>
    /// Floor B-tree lookup within the current <see cref="Bound"/>. On success sets
    /// <see cref="_bound"/> to the floor entry's value region (largest stored key ≤ <paramref name="key"/>)
    /// and returns the prior bound via <paramref name="previousBound"/>. Returns false if the HSST
    /// is empty or <paramref name="key"/> precedes every entry.
    /// </summary>
    public bool TrySeekFloor(scoped ReadOnlySpan<byte> key, out Bound previousBound) =>
        TrySeekCore(key, exactMatch: false, out previousBound);

    private bool TrySeekCore(scoped ReadOnlySpan<byte> key, bool exactMatch, out Bound previousBound)
    {
        previousBound = _bound;

        if (_bound.Length < 2) return false;

        // IndexType byte is the last byte of the HSST.
        Span<byte> idxType = stackalloc byte[1];
        if (!_reader.TryRead(_bound.Offset + _bound.Length - 1, idxType)) return false;
        switch ((IndexType)idxType[0])
        {
            case IndexType.BTree:
                if (HsstBTreeReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, out Bound btreeBound))
                {
                    _bound = btreeBound;
                    return true;
                }
                return false;
            case IndexType.PackedArray:
                if (HsstPackedArrayReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, out Bound flatBound))
                {
                    _bound = flatBound;
                    return true;
                }
                return false;
            case IndexType.ByteTagMap:
                if (HsstByteTagMapReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, out Bound tagBound))
                {
                    _bound = tagBound;
                    return true;
                }
                return false;
            case IndexType.DenseByteIndex:
                if (HsstDenseByteIndexReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, out Bound denseBound))
                {
                    _bound = denseBound;
                    return true;
                }
                return false;
            default: return false;
        }
    }

    public void Dispose()
    {
        // No owned resources; pins are released per-iteration in the per-layout readers.
    }
}
