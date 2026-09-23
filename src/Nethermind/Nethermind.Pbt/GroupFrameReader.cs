// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

// The only stack buffer, the branch encoding in GetHash, is fully written before it is read.
[SkipLocalsInit]
internal struct GroupFrameReader<TKey, TPath> : IDisposable
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    private readonly IPbtStore _store;
    private ValueHash256 _groupHash;
    private readonly TrieUpdaterMetrics? _metrics;
    private RefCountingMemory? _lease;
    private DescendantBuffer _descendantBytes;
    private bool _loaded;
    private OffsetBuffer _offsets;
    private LengthBuffer _lengths;
    private HashBuffer _hashes;
    private uint _hashed;
    private uint _stored;
    internal uint Taken;

    internal GroupFrameReader(IPbtStore store, int bitDepth, in ValueHash256 groupHash, TrieUpdaterMetrics? metrics)
        : this(store, bitDepth, metrics) => _groupHash = groupHash;

    /// <summary>Opens a frame whose group key is only established once the group turns out to exist.</summary>
    internal GroupFrameReader(IPbtStore store, int bitDepth, TrieUpdaterMetrics? metrics)
    {
        BitDepth = bitDepth;
        _store = store;
        _metrics = metrics;
        metrics?.IncrementGroupFrameResolutions();
    }

    /// <summary>Keys this frame's group, which only an unresolved frame still needs.</summary>
    internal void SetGroupHash(in ValueHash256 hash)
    {
        Debug.Assert(!_loaded, "A resolved frame never loads its group.");
        _groupHash = hash;
    }

    private void EnsureLoaded(scoped in PbtTraversalPath path)
    {
        Debug.Assert(path.BitDepth == BitDepth);
        if (_loaded) return;
        _metrics?.IncrementPhysicalGroupFetches();
        _lease = _store.GetNodeGroup(path, _groupHash);
        _loaded = true;
        if (_lease is null) return;
        try
        {
            _metrics?.IncrementGroupParses();
            PbtNodeGroupReader reader = PbtNodeGroupReader.FromValidated(path, _lease.GetSpan());
            for (int slot = 0; slot < PbtNodeGroupCodec.DescendantSlots; slot++) _descendantBytes[slot] = reader.DescendantBytes(slot);
            for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
            {
                if (position == PbtFourLevelGroupGeometry.RootPosition && BitDepth != 0) continue;
                if (!reader.TryGetNodeRange(position, out int offset, out int length)) continue;
                _offsets[position] = offset;
                _lengths[position] = length;
                _stored |= 1u << position;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int BitDepth { get; }

    /// <summary>Whether the frame has been loaded or declared absent, so its descendant sizes are final.</summary>
    internal readonly bool IsResolved => _loaded;

    /// <summary>The stored payload's length, or zero when nothing was loaded.</summary>
    internal readonly int PayloadLength => _lease?.GetSpan().Length ?? 0;

    /// <summary>The summed payload lengths of the groups physically stored below boundary slot <paramref name="slot"/>, without loading.</summary>
    /// <remarks>Read only once the frame is resolved, or for a frame whose input is empty or a leaf, which has no descendants.</remarks>
    internal readonly long DescendantBytes(int slot) => _descendantBytes[slot];

    /// <summary>Declares that no group is stored below this frame's boundary node, so a later load is a no-op instead of a store miss.</summary>
    /// <remarks>
    /// A group holds the nodes strictly below its boundary node, so a boundary node with nothing stored below it
    /// owns an empty group, which <see cref="PbtNodeGroupWriter{TPath}.Detach"/> turned into a deletion. Such a
    /// group has no descendant groups either, so every slot keeps its zero size.
    /// </remarks>
    internal void DeclareAbsent()
    {
        Debug.Assert(!_loaded, "A frame is declared absent before it is loaded.");
        _loaded = true;
    }

    /// <summary><see cref="DeclareAbsent"/>, recording the descendants the group's spanning branch keeps below <paramref name="slot"/>.</summary>
    /// <remarks>
    /// A branch whose prefix spans past this group is the only node under its parent's boundary slot, so the
    /// parent's size for that slot is exactly this group's size below the branch.
    /// </remarks>
    internal void InheritDescendants(int slot, long descendantBytes)
    {
        DeclareAbsent();
        _descendantBytes[slot] = descendantBytes;
    }

    internal ReadOnlyMemory<byte> GetEncoding(scoped in PbtTraversalPath path, int position)
    {
        EnsureLoaded(path);
        return _lengths[position] == 0 ? default : _lease!.Memory.Slice(_offsets[position], _lengths[position]);
    }

    internal int CopyRange(scoped in PbtTraversalPath path, PbtNodeGroupWriter<TPath> writer, int startPosition, int endPosition)
    {
        if (startPosition == endPosition) return 0;
        EnsureLoaded(path);
        while (startPosition < endPosition && _lengths[startPosition] == 0) startPosition++;
        if (startPosition == endPosition) return 0;
        int lastPosition = endPosition - 1;
        while (_lengths[lastPosition] == 0) lastPosition--;
        int startOffset = _offsets[startPosition];
        ReadOnlySpan<byte> entries = _lease!.GetSpan().Slice(startOffset,
            _offsets[lastPosition] + _lengths[lastPosition] - startOffset);
        return writer.CopyRange(entries, _offsets, _lengths, startPosition, lastPosition);
    }

    /// <summary>Takes the node stored at <paramref name="position"/> as the bytes composition writes back unchanged, or empty when the group stores none there.</summary>
    /// <remarks>The hash is the seeded link hash where a link named this node; composition otherwise hashes the encoding the once it is needed.</remarks>
    internal TrieUpdater<TKey, TPath>.DirectCopySubtree TakeDirectCopy(scoped in PbtTraversalPath path, int position)
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(path, position);
        if (encoding.IsEmpty) return default;
        Taken |= 1U << position;
        return new(encoding, PbtFourLevelGroupGeometry.LocalPathOf(position), SeededHash(position));
    }

    /// <summary>Records the hash a parent node holds for <paramref name="position"/>, so composing it needs no rehash.</summary>
    internal void SeedHash(int position, in ValueHash256 hash)
    {
        _hashes[position] = hash;
        _hashed |= 1u << position;
    }

    /// <summary>The hash a parent's link held for the node at <paramref name="position"/>, or default when no link named it.</summary>
    internal readonly ValueHash256 SeededHash(int position) => (_hashed & (1u << position)) != 0 ? _hashes[position] : default;

    /// <summary>The positions this frame stores an encoding at, loading it if it has not been read yet.</summary>
    internal uint StoredPositions(scoped in PbtTraversalPath path)
    {
        EnsureLoaded(path);
        return _stored;
    }

    /// <summary>Takes the group's own root, whose hash is this frame's identity.</summary>
    internal TrieUpdater<TKey, TPath>.BoundaryNode TakeRoot(scoped in PbtTraversalPath path)
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(path, PbtFourLevelGroupGeometry.RootPosition);
        Taken |= 1U << PbtFourLevelGroupGeometry.RootPosition;
        if (encoding.IsEmpty) return default;
        return PbtNodeReader.FromValidated(encoding.Span).IsLeaf
            ? new TrieUpdater<TKey, TPath>.BoundaryNode(encoding, _groupHash)
            : new TrieUpdater<TKey, TPath>.BoundaryNode(encoding, BitDepth, _groupHash);
    }

    /// <summary>Takes the boundary node stored at <paramref name="position"/>, whose hash decomposition already knows.</summary>
    /// <remarks>The hash is the seeded link hash where a link named this node, and otherwise the one hash its encoding needs.</remarks>
    internal TrieUpdater<TKey, TPath>.BoundaryNode TakeBoundaryNode(scoped in PbtTraversalPath path, int position)
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(path, position);
        if (encoding.IsEmpty) throw new InvalidDataException("A referenced PBT node is missing.");
        Taken |= 1U << position;
        if (PbtNodeReader.FromValidated(encoding.Span).IsLeaf) return new(encoding, _groupHash);
        return new(encoding, BitDepth + PbtFourLevelGroupGeometry.LocalPathOf(position).Length, GetHash(path, position));
    }

    /// <summary>Takes the leaf inlined in the branch at <paramref name="position"/>, which stores no node of its own.</summary>
    internal TrieUpdater<TKey, TPath>.BoundaryNode TakeInlineLeaf(scoped in PbtTraversalPath path, int position, bool right) =>
        new(GetEncoding(path, position), right);

    private ValueHash256 GetHash(scoped in PbtTraversalPath path, int position)
    {
        uint bit = 1u << position;
        if ((_hashed & bit) != 0) return _hashes[position];
        ReadOnlyMemory<byte> encoding = GetEncoding(path, position);
        ValueHash256 hash = default;
        if (!encoding.IsEmpty)
        {
            _metrics?.IncrementNodeHashes();
            hash = PbtNodeCodec.Hash(PbtNodeReader.FromValidated(encoding.Span));
        }
        else if (PbtFourLevelGroupGeometry.WidthOf(position) is int width and > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
        {
            GetChildHashes(path, position - width, position - 1, out ValueHash256 left, out ValueHash256 right);
            if (left != default && right != default)
            {
                Span<byte> branch = stackalloc byte[67];
                PbtNodeCodec.CreateBranchEncoding(branch, 0, left, right);
                _metrics?.IncrementNodeHashes();
                hash = Blake3Hash.Hash(branch);
            }
        }
        _hashes[position] = hash;
        _hashed |= bit;
        return hash;
    }

    /// <summary>
    /// <see cref="GetHash"/> for both children of an omitted branch; two stored encodings that still need
    /// hashing are hashed together.
    /// </summary>
    internal void GetChildHashes(scoped in PbtTraversalPath path, int leftPosition, int rightPosition, out ValueHash256 left, out ValueHash256 right)
    {
        uint bits = (1u << leftPosition) | (1u << rightPosition);
        if ((_hashed & bits) == 0)
        {
            ReadOnlyMemory<byte> leftEncoding = GetEncoding(path, leftPosition);
            ReadOnlyMemory<byte> rightEncoding = GetEncoding(path, rightPosition);
            if (!leftEncoding.IsEmpty && !rightEncoding.IsEmpty)
            {
                _metrics?.AddNodeHashes(2);
                Blake3Hash.HashTwo(PbtNodeReader.FromValidated(leftEncoding.Span).Preimage, PbtNodeReader.FromValidated(rightEncoding.Span).Preimage, out left, out right);
                _hashes[leftPosition] = left;
                _hashes[rightPosition] = right;
                _hashed |= bits;
                return;
            }
        }
        left = GetHash(path, leftPosition);
        right = GetHash(path, rightPosition);
    }

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
    private struct HashBuffer
    {
        private ValueHash256 _element;
    }

    [InlineArray(PbtNodeGroupCodec.DescendantSlots)]
    private struct DescendantBuffer
    {
        private long _element;
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
