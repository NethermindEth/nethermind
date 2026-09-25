// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Reads a node group that is physically stored, as one frame of a fold.</summary>
/// <remarks>
/// A frame reader only ever wraps a stored payload: never construct one for a group that does not exist. A group the
/// boundary node proves absent is folded through <see cref="AbsentGroupFrame{TKey, TPath}"/> instead, and the tree
/// root's group, the only one whose existence is learned from the store, is probed with <see cref="TryLoad"/>.
/// </remarks>
internal struct GroupFrameReader<TKey, TPath> : IGroupFrame<TKey, TPath>, IDisposable
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    private readonly ValueHash256 _groupHash;
    private RefCountingMemory? _lease;
    private OffsetBuffer _offsets;
    private LengthBuffer _lengths;
    private readonly uint _stored;

    /// <summary>Loads the group stored at <paramref name="path"/>, keyed by <paramref name="groupHash"/>.</summary>
    /// <exception cref="InvalidDataException">The store holds no group at <paramref name="path"/>.</exception>
    internal GroupFrameReader(IPbtStore store, scoped in PbtTraversalPath path, in ValueHash256 groupHash, TrieUpdaterMetrics? metrics)
        : this(Fetch(store, path, groupHash, metrics) ?? throw new InvalidDataException("A referenced PBT node group is missing."), path.BitDepth, groupHash, metrics) { }

    private GroupFrameReader(RefCountingMemory lease, int bitDepth, in ValueHash256 groupHash, TrieUpdaterMetrics? metrics)
    {
        BitDepth = bitDepth;
        _groupHash = groupHash;
        _lease = lease;
        metrics?.IncrementGroupFrameResolutions();
        metrics?.IncrementGroupParses();
        try
        {
            // The store validated the payload, so the offsets are walked by the availability bits alone, which a small group has few of.
            ReadOnlySpan<byte> payload = lease.GetSpan();
            ReadOnlySpan<byte> entries = payload[PbtNodeGroupCodec.HeaderLength..];
            uint availability = PbtNodeGroupCodec.ReadAvailability(entries);
            Debug.Assert(bitDepth == 0 || (availability & (1u << PbtFourLevelGroupGeometry.RootPosition)) == 0, "Only the root group stores the root position.");
            int entriesEnd = PbtNodeGroupCodec.HeaderLength + entries.Length - PbtNodeGroupCodec.GetTrailerLength(availability, PbtNodeGroupCodec.ReadDescendantMask(payload));
            ReadOnlySpan<byte> offsets = payload[entriesEnd..];
            int previous = -1;
            for (uint remaining = availability; remaining != 0; remaining &= remaining - 1)
            {
                int position = BitOperations.TrailingZeroCount(remaining);
                _offsets[position] = PbtNodeGroupCodec.HeaderLength + BinaryPrimitives.ReadUInt16LittleEndian(offsets);
                offsets = offsets[sizeof(ushort)..];
                if (previous >= 0) _lengths[previous] = _offsets[position] - _offsets[previous];
                previous = position;
            }
            if (previous >= 0) _lengths[previous] = entriesEnd - _offsets[previous];
            _stored = availability;
        }
        catch
        {
            ((IDisposable)lease).Dispose();
            throw;
        }
    }

    /// <summary>Loads the group stored at <paramref name="path"/>, or reports that the store holds none.</summary>
    /// <remarks>
    /// Only the tree root's group may be missing, when the tree is empty. Its absence cannot be derived from
    /// <paramref name="groupHash"/>, which may be stale or default when unknown, so the store is asked.
    /// </remarks>
    internal static bool TryLoad(IPbtStore store, scoped in PbtTraversalPath path, in ValueHash256 groupHash, TrieUpdaterMetrics? metrics,
        out GroupFrameReader<TKey, TPath> reader)
    {
        RefCountingMemory? lease = Fetch(store, path, groupHash, metrics);
        reader = lease is null ? default : new(lease, path.BitDepth, groupHash, metrics);
        return lease is not null;
    }

    private static RefCountingMemory? Fetch(IPbtStore store, scoped in PbtTraversalPath path, in ValueHash256 groupHash, TrieUpdaterMetrics? metrics)
    {
        metrics?.IncrementPhysicalGroupFetches();
        return store.GetNodeGroup(path, groupHash);
    }

    public int BitDepth { get; }

    /// <inheritdoc/>
    public readonly int PayloadLength => _lease!.GetSpan().Length;

    /// <inheritdoc/>
    public readonly long DescendantBytes(int slot)
    {
        ReadOnlySpan<byte> payload = _lease!.GetSpan();
        ushort descendantMask = PbtNodeGroupCodec.ReadDescendantMask(payload);
        return (descendantMask & (1 << slot)) == 0 ? 0 : PbtNodeGroupCodec.ReadDescendantBytes(payload, descendantMask, slot);
    }

    /// <inheritdoc/>
    public readonly ushort DescendantMask => PbtNodeGroupCodec.ReadDescendantMask(_lease!.GetSpan());

    /// <inheritdoc/>
    public readonly ReadOnlyMemory<byte> GetEncoding(int position) =>
        _lengths[position] == 0 ? default : _lease!.Memory.Slice(_offsets[position], _lengths[position]);

    /// <inheritdoc/>
    public readonly int CopyRange(PbtNodeGroupWriter<TPath> writer, int startPosition, int endPosition)
    {
        if (startPosition == endPosition) return 0;
        while (startPosition < endPosition && _lengths[startPosition] == 0) startPosition++;
        if (startPosition == endPosition) return 0;
        int lastPosition = endPosition - 1;
        while (_lengths[lastPosition] == 0) lastPosition--;
        int startOffset = _offsets[startPosition];
        ReadOnlySpan<byte> entries = _lease!.GetSpan().Slice(startOffset,
            _offsets[lastPosition] + _lengths[lastPosition] - startOffset);
        return writer.CopyRange(entries, _offsets, _lengths, startPosition, lastPosition);
    }

    /// <inheritdoc/>
    public readonly uint StoredPositions => _stored;

    /// <summary>Takes the group's own root, whose hash is this frame's identity.</summary>
    internal readonly TrieUpdater<TKey, TPath>.BoundaryNode TakeRoot()
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(PbtFourLevelGroupGeometry.RootPosition);
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
        return new(encoding, BitDepth + PbtFourLevelGroupGeometry.LocalPathOf(position).Length, hash);
    }

    /// <inheritdoc/>
    public readonly TrieUpdater<TKey, TPath>.BoundaryNode TakeInlineLeaf(int position, bool right) =>
        new(GetEncoding(position), right);

    public void Dispose()
    {
        ((IDisposable?)_lease)?.Dispose();
        _lease = null;
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

    [InlineArray(PbtNodeGroupCodec.PositionCount)]
    private struct OffsetBuffer
    {
        private int _element;
    }

    [InlineArray(PbtNodeGroupCodec.PositionCount)]
    private struct LengthBuffer
    {
        private int _element;
    }
}
