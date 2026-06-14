// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.State.Flat.Hsst.BTree;
using Nethermind.State.Flat.Hsst.PackedArray;
using Nethermind.State.Flat.Hsst.DenseByteIndex;
using Nethermind.State.Flat.Hsst.TwoByteSlot;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Non-span HSST reader generic over <typeparamref name="TReader"/>. Symmetric to
/// <see cref="HsstBTreeBuilder{TWriter}"/>: any byte source that implements
/// <see cref="IHsstByteReader"/> works — mmap, heap array, file handle, etc.
///
/// Maintains an active <see cref="Bound"/> (absolute offset+length within the reader).
/// <see cref="TrySeek"/> dispatches by the trailing <see cref="IndexType"/> byte into the
/// per-layout reader (<see cref="HsstBTreeReader"/>, <see cref="HsstPackedArrayReader"/>,
/// <see cref="HsstDenseByteIndexReader"/>) and repositions the bound to the matched entry's
/// value region, also returning that bound via <c>out matched</c>. To save/restore
/// scope across sibling seeks, capture <see cref="GetBound"/> beforehand and re-enter via
/// the <c>(reader, bound)</c> constructor.
///
/// The keys-first two-byte-slot variants (<see cref="IndexType.TwoByteSlotValue"/> /
/// <see cref="IndexType.TwoByteSlotValueLarge"/>) carry their <see cref="IndexType"/> byte
/// at byte 0, not the tail; they are always nested and reached via
/// <see cref="TrySeekTwoByteSlot"/>, which dispatches forward with no tail seek.
/// </summary>
public ref struct HsstReader<TReader, TPin>(scoped in TReader reader, Bound initialBound) : IDisposable
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private TReader _reader = reader;
    private Bound _bound = initialBound;

    public HsstReader(scoped in TReader reader) : this(reader, new Bound(0, reader.Length)) { }

    public readonly Bound GetBound() => _bound;

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

    [SkipLocalsInit]
    private bool TrySeekCore(scoped ReadOnlySpan<byte> key, bool exactMatch, out Bound matched)
    {
        if (_bound.Length < 2) { matched = default; return false; }

        // IndexType byte is the last byte of the HSST.
        byte idxType = 0;
        if (!_reader.TryRead(_bound.Offset + _bound.Length - 1, new Span<byte>(ref idxType))) { matched = default; return false; }
        switch ((IndexType)idxType)
        {
            case IndexType.BTree:
                if (HsstBTreeReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, keyFirst: false, out Bound btreeBound))
                {
                    _bound = btreeBound;
                    matched = btreeBound;
                    return true;
                }
                matched = default;
                return false;
            case IndexType.BTreeKeyFirst:
                if (HsstBTreeReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, keyFirst: true, out Bound btreeKfBound))
                {
                    _bound = btreeKfBound;
                    matched = btreeKfBound;
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
            case IndexType.DenseByteIndex:
                if (HsstDenseByteIndexReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, out Bound denseBound))
                {
                    _bound = denseBound;
                    matched = denseBound;
                    return true;
                }
                matched = default;
                return false;
            // TwoByteSlotValue / TwoByteSlotValueLarge are keys-first nested blobs whose
            // IndexType byte leads the blob (byte 0), not the tail. They are never
            // top-level, so they cannot be reached by this last-byte dispatch — callers
            // that descend into one use TrySeekTwoByteSlot instead.
            default:
                matched = default;
                return false;
        }
    }

    /// <summary>
    /// Exact-match lookup over a nested keys-first two-byte-slot HSST
    /// (<see cref="IndexType.TwoByteSlotValue"/> / <see cref="IndexType.TwoByteSlotValueLarge"/>),
    /// whose <see cref="IndexType"/> byte leads the blob at byte 0. Unlike <see cref="TrySeek"/>
    /// this dispatches on the first byte, so the lookup is a single forward read with no tail
    /// seek — the caller must already know the current bound is one of these two variants.
    /// </summary>
    public bool TrySeekTwoByteSlot(scoped ReadOnlySpan<byte> key, out Bound matched) =>
        TrySeekTwoByteSlotCore(key, exactMatch: true, out matched);

    /// <summary>Floor variant of <see cref="TrySeekTwoByteSlot"/> (largest stored key ≤ <paramref name="key"/>).</summary>
    internal bool TrySeekTwoByteSlotFloor(scoped ReadOnlySpan<byte> key, out Bound matched) =>
        TrySeekTwoByteSlotCore(key, exactMatch: false, out matched);

    [SkipLocalsInit]
    private bool TrySeekTwoByteSlotCore(scoped ReadOnlySpan<byte> key, bool exactMatch, out Bound matched)
    {
        if (_bound.Length < 2) { matched = default; return false; }

        // IndexType byte leads the blob — read byte 0 forward, no tail seek.
        byte idxType = 0;
        if (!_reader.TryRead(_bound.Offset, new Span<byte>(ref idxType))) { matched = default; return false; }
        switch ((IndexType)idxType)
        {
            case IndexType.TwoByteSlotValue:
                if (HsstTwoByteSlotValueReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, offsetSize: 2, out Bound tbsvBound))
                {
                    _bound = tbsvBound;
                    matched = tbsvBound;
                    return true;
                }
                matched = default;
                return false;
            case IndexType.TwoByteSlotValueLarge:
                if (HsstTwoByteSlotValueReader.TrySeek<TReader, TPin>(in _reader, _bound, key, exactMatch, offsetSize: 3, out Bound tbsvLargeBound))
                {
                    _bound = tbsvLargeBound;
                    matched = tbsvLargeBound;
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
