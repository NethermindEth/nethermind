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
    private readonly ValueHash256 _groupHash;
    private readonly TrieUpdaterMetrics? _metrics;
    private RefCountingMemory? _lease;
    private long _subtreeBytes;
    private bool _loaded;
    private OffsetBuffer _offsets;
    private LengthBuffer _lengths;
    private HashBuffer _hashes;
    private uint _hashed;
    internal uint Taken;

    internal GroupFrameReader(IPbtStore store, int bitDepth, in ValueHash256 groupHash, TrieUpdaterMetrics? metrics)
    {
        BitDepth = bitDepth;
        _groupHash = groupHash;
        _store = store;
        _metrics = metrics;
        metrics?.IncrementGroupFrameResolutions();
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
            PbtNodeGroupReader reader = new(path, _lease.GetSpan());
            _subtreeBytes = reader.SubtreeBytes;
            for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
            {
                if (position == PbtFourLevelGroupGeometry.RootPosition && BitDepth != 0) continue;
                if (!reader.TryGetNodeRange(position, out int offset, out int length)) continue;
                _offsets[position] = offset;
                _lengths[position] = length;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int BitDepth { get; }

    /// <summary>Whether a stored payload was loaded for this frame.</summary>
    /// <remarks>
    /// A frame that never loaded has no stored group: every node of the group is a descendant of the frame's
    /// input subtree, and an input whose children lie inside the group is decomposed or bulk-copied through this
    /// reader before the frame is published. The size accessors below then report an absent group.
    /// </remarks>
    internal readonly bool HasPayload => _lease is not null;

    /// <summary>The stored payload's length, or zero when nothing was loaded.</summary>
    internal readonly int PayloadLength => _lease?.GetSpan().Length ?? 0;

    /// <summary>The stored payload's subtree size minus its own length, or zero when nothing was loaded.</summary>
    internal readonly long DescendantBytes => _subtreeBytes - PayloadLength;

    /// <summary>The stored payload's subtree size, loading the group; zero when nothing is stored.</summary>
    internal long SubtreeBytes(scoped in PbtTraversalPath path)
    {
        EnsureLoaded(path);
        return _subtreeBytes;
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

    internal TrieUpdater<TKey, TPath>.Subtree Acquire(scoped in PbtTraversalPath path, int position)
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(path, position);
        ValueHash256 knownHash = (_hashed & (1u << position)) != 0 ? _hashes[position] : default;
        if (encoding.IsEmpty)
        {
            int width = PbtFourLevelGroupGeometry.WidthOf(position);
            if (width is 1 or PbtFourLevelGroupGeometry.BoundarySlots) return default;
            ValueHash256 left = GetHash(path, position - width);
            ValueHash256 right = GetHash(path, position - 1);
            return left == default || right == default ? default : new(PbtFourLevelGroupGeometry.LocalPathOf(position), left, right, knownHash);
        }
        NodeGroupPath nodePath = PbtNodeReader.FromValidated(encoding.Span).IsLeaf ? default : PbtFourLevelGroupGeometry.LocalPathOf(position);
        return new(encoding, nodePath, knownHash);
    }

    /// <summary>Records the hash a parent node holds for <paramref name="position"/>, so acquiring it needs no rehash.</summary>
    internal void SeedHash(int position, in ValueHash256 hash)
    {
        _hashes[position] = hash;
        _hashed |= 1u << position;
    }

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
            ValueHash256 left = GetHash(path, position - width);
            ValueHash256 right = GetHash(path, position - 1);
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

    internal TrieUpdater<TKey, TPath>.Subtree Take(scoped in PbtTraversalPath path, PbtNodeGroupWriter<TPath> writer, int position, bool allowAbsent = false)
    {
        Debug.Assert(position > writer.LastPosition, "Cannot take a PBT node after its output position has passed.");
        TrieUpdater<TKey, TPath>.Subtree node = (Taken & (1U << position)) == 0
            ? Acquire(path, position)
            : default;
        if (node.IsEmpty && !allowAbsent) throw new InvalidDataException("A referenced PBT node is missing.");
        Taken |= 1U << position;
        return node;
    }

}
