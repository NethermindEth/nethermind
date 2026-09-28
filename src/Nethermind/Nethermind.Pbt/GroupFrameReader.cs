// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Reads a node group that is physically stored, as one frame of a fold.</summary>
/// <remarks>
/// A frame reader only ever wraps a stored payload: never construct one for a group that does not exist. A group the
/// boundary node proves absent is folded through <see cref="AbsentGroupFrame{TKey, TPath}"/> instead, and the tree
/// root's group, the only one whose existence is learned from the store, is probed with <see cref="TryLoad"/>.
/// Frames nest once per fold level, so a group wider than <see cref="PbtGroupGeometry.InlinePositionCapacity"/> positions
/// rents its offset table rather than holding it inline, and <see cref="Dispose"/> returns it.
/// </remarks>
internal struct GroupFrameReader<TKey, TPath> : IGroupFrame<TKey, TPath>, IDisposable
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    private readonly ValueHash256 _groupHash;
    private RefCountingMemory? _lease;
    private OffsetBuffer _inlineOffsets;
    private OffsetBuffer _inlineLengths;
    private int[]? _rented;
    private readonly PbtBitmap _stored;
    private readonly PbtBitmap _descendantMask;

    /// <summary>Loads the group stored at <paramref name="path"/>, keyed by <paramref name="groupHash"/>.</summary>
    /// <exception cref="InvalidDataException">The store holds no group at <paramref name="path"/>.</exception>
    internal GroupFrameReader(IPbtStore store, scoped in PbtTraversalPath path, in ValueHash256 groupHash)
        : this(store.GetNodeGroup(path, groupHash) ?? throw new InvalidDataException("A referenced PBT node group is missing."), path.BitDepth, groupHash) { }

    private GroupFrameReader(RefCountingMemory lease, int bitDepth, in ValueHash256 groupHash)
    {
        BitDepth = bitDepth;
        _groupHash = groupHash;
        _lease = lease;
        try
        {
            // The store validated the payload, so the offsets are walked by the availability bits alone, which a small group has few of.
            ReadOnlySpan<byte> payload = lease.GetSpan();
            ReadOnlySpan<byte> entries = payload[PbtNodeGroupCodec.HeaderLength..];
            PbtNodeGroupCodec.ReadAvailability(entries, out _stored);
            PbtNodeGroupCodec.ReadDescendantMask(payload, out _descendantMask);
            Debug.Assert(bitDepth == 0 || !_stored.IsSet(PbtGroupGeometry.RootPosition), "Only the root group stores the root position.");
            int entriesEnd = PbtNodeGroupCodec.HeaderLength + entries.Length - PbtNodeGroupCodec.GetTrailerLength(_stored, payload);
            ReadOnlySpan<byte> offsets = payload[entriesEnd..];
            scoped Span<int> offsetTable, lengths;
            if (PbtGroupGeometry.PositionCount <= PbtGroupGeometry.InlinePositionCapacity)
            {
                offsetTable = _inlineOffsets;
                lengths = _inlineLengths;
            }
            else
            {
                _rented = ArrayPool<int>.Shared.Rent(2 * PbtGroupGeometry.PositionCount);
                offsetTable = _rented;
                lengths = _rented.AsSpan(PbtGroupGeometry.PositionCount);
            }
            offsetTable = offsetTable[..PbtGroupGeometry.PositionCount];
            lengths = lengths[..PbtGroupGeometry.PositionCount];
            lengths.Clear();
            int previous = -1;
            for (int position = _stored.NextSetBit(0); position >= 0; position = _stored.NextSetBit(position + 1))
            {
                offsetTable[position] = PbtNodeGroupCodec.HeaderLength + PbtNodeGroupCodec.ReadOffset(offsets);
                offsets = offsets[PbtNodeGroupCodec.OffsetLength..];
                if (previous >= 0) lengths[previous] = offsetTable[position] - offsetTable[previous];
                previous = position;
            }
            if (previous >= 0) lengths[previous] = entriesEnd - offsetTable[previous];
        }
        catch
        {
            ((IDisposable)lease).Dispose();
            if (_rented is not null) ArrayPool<int>.Shared.Return(_rented);
            throw;
        }
    }

    [UnscopedRef]
    private readonly ReadOnlySpan<int> Offsets => PbtGroupGeometry.PositionCount <= PbtGroupGeometry.InlinePositionCapacity
        ? ((ReadOnlySpan<int>)_inlineOffsets)[..PbtGroupGeometry.PositionCount]
        : _rented.AsSpan(0, PbtGroupGeometry.PositionCount);

    [UnscopedRef]
    private readonly ReadOnlySpan<int> Lengths => PbtGroupGeometry.PositionCount <= PbtGroupGeometry.InlinePositionCapacity
        ? ((ReadOnlySpan<int>)_inlineLengths)[..PbtGroupGeometry.PositionCount]
        : _rented.AsSpan(PbtGroupGeometry.PositionCount, PbtGroupGeometry.PositionCount);

    /// <summary>Loads the group stored at <paramref name="path"/>, or reports that the store holds none.</summary>
    /// <remarks>
    /// Only the tree root's group may be missing, when the tree is empty. Its absence cannot be derived from
    /// <paramref name="groupHash"/>, which may be stale or default when unknown, so the store is asked.
    /// </remarks>
    internal static bool TryLoad(IPbtStore store, scoped in PbtTraversalPath path, in ValueHash256 groupHash,
        out GroupFrameReader<TKey, TPath> reader)
    {
        RefCountingMemory? lease = store.GetNodeGroup(path, groupHash);
        reader = lease is null ? default : new(lease, path.BitDepth, groupHash);
        return lease is not null;
    }

    public int BitDepth { get; }

    /// <inheritdoc/>
    public readonly int PayloadLength => _lease!.GetSpan().Length;

    /// <inheritdoc/>
    public readonly long DescendantBytes(int slot) =>
        !_descendantMask.IsSet(slot) ? 0 : PbtNodeGroupCodec.ReadDescendantBytes(_lease!.GetSpan(), _descendantMask, slot);

    /// <inheritdoc/>
    public readonly PbtBitmap DescendantMask => _descendantMask;

    /// <inheritdoc/>
    public readonly ReadOnlyMemory<byte> GetEncoding(int position)
    {
        int length = Lengths[position];
        return length == 0 ? default : _lease!.Memory.Slice(Offsets[position], length);
    }

    /// <inheritdoc/>
    public readonly int CopyRange(PbtNodeGroupWriter<TPath> writer, int startPosition, int endPosition)
    {
        if (startPosition == endPosition) return 0;
        ReadOnlySpan<int> offsets = Offsets;
        ReadOnlySpan<int> lengths = Lengths;
        while (startPosition < endPosition && lengths[startPosition] == 0) startPosition++;
        if (startPosition == endPosition) return 0;
        int lastPosition = endPosition - 1;
        while (lengths[lastPosition] == 0) lastPosition--;
        int startOffset = offsets[startPosition];
        ReadOnlySpan<byte> entries = _lease!.GetSpan().Slice(startOffset,
            offsets[lastPosition] + lengths[lastPosition] - startOffset);
        return writer.CopyRange(entries, offsets, lengths, startPosition, lastPosition);
    }

    /// <inheritdoc/>
    public readonly PbtBitmap StoredPositions => _stored;

    /// <summary>Takes the group's own root, whose hash is this frame's identity.</summary>
    internal readonly TrieUpdater<TKey, TPath>.BoundaryNode TakeRoot()
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(PbtGroupGeometry.RootPosition);
        if (encoding.IsEmpty) return default;
        return PbtNodeReader.FromValidated(encoding.Span).IsLeaf
            ? new TrieUpdater<TKey, TPath>.BoundaryNode(encoding, _groupHash)
            : new TrieUpdater<TKey, TPath>.BoundaryNode(encoding, BitDepth, _groupHash);
    }

    /// <inheritdoc/>
    public readonly TrieUpdater<TKey, TPath>.BoundaryNode TakeBoundaryNode(int position, in ValueHash256 hash)
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(position);
        if (encoding.IsEmpty) throw new InvalidDataException("A referenced PBT node is missing.");
        if (PbtNodeReader.FromValidated(encoding.Span).IsLeaf) return new(encoding, _groupHash);
        return new(encoding, BitDepth + PbtGroupGeometry.LocalPathOf(position).Length, hash);
    }

    /// <inheritdoc/>
    public readonly TrieUpdater<TKey, TPath>.BoundaryNode TakeInlineLeaf(int position, bool right) =>
        new(GetEncoding(position), BitDepth + PbtGroupGeometry.LocalPathOf(position).Length, right);

    public void Dispose()
    {
        ((IDisposable?)_lease)?.Dispose();
        _lease = null;
        if (_rented is null) return;
        ArrayPool<int>.Shared.Return(_rented);
        _rented = null;
    }

    /// <summary>Releases the actual mutable frames, including payloads loaded after this scope was opened.</summary>
    internal readonly ref struct Scope(Span<GroupFrameReader<TKey, TPath>> readers) : IDisposable
    {
        private readonly Span<GroupFrameReader<TKey, TPath>> _readers = readers;

        internal Scope(ref GroupFrameReader<TKey, TPath> reader) : this(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref reader, 1)) { }

        public void Dispose()
        {
            foreach (ref GroupFrameReader<TKey, TPath> reader in _readers) reader.Dispose();
        }
    }

    [InlineArray(PbtGroupGeometry.InlinePositionCapacity)]
    private struct OffsetBuffer
    {
        private int _element;
    }
}
