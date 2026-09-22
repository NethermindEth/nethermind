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
    private DescendantBuffer _descendantBytes;
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
            for (int slot = 0; slot < PbtNodeGroupCodec.DescendantSlots; slot++) _descendantBytes[slot] = reader.DescendantBytes(slot);
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

    /// <summary>Whether the frame has been loaded or declared absent, so its descendant sizes are final.</summary>
    internal readonly bool IsResolved => _loaded;

    /// <summary>The stored payload's length, or zero when nothing was loaded.</summary>
    internal readonly int PayloadLength => _lease?.GetSpan().Length ?? 0;

    /// <summary>The summed payload lengths of the groups physically stored below boundary slot <paramref name="slot"/>, without loading.</summary>
    /// <remarks>Read only once the frame is resolved, or for a frame whose input is empty or a leaf, which has no descendants.</remarks>
    internal readonly long DescendantBytes(int slot) => _descendantBytes[slot];

    /// <summary>Declares the group absent and records the descendants its spanning branch keeps below <paramref name="slot"/>.</summary>
    /// <remarks>
    /// A branch whose prefix spans past this group is the only node under its parent's boundary slot, so the
    /// parent's size for that slot is exactly this group's size below the branch. Declaring the group absent
    /// makes any later load a no-op instead of a store miss.
    /// </remarks>
    internal void InheritDescendants(int slot, long descendantBytes)
    {
        Debug.Assert(!_loaded, "Descendants are inherited before the frame is loaded.");
        _descendantBytes[slot] = descendantBytes;
        _loaded = true;
    }

    /// <summary>The stored payload's length plus its descendant sizes, loading the group; zero when nothing is stored.</summary>
    internal long SubtreeBytes(scoped in PbtTraversalPath path)
    {
        EnsureLoaded(path);
        long subtreeBytes = PayloadLength;
        foreach (long slotBytes in _descendantBytes) subtreeBytes += slotBytes;
        return subtreeBytes;
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
            GetChildHashes(path, position - width, position - 1, out ValueHash256 left, out ValueHash256 right);
            return left == default || right == default ? default : new(PbtFourLevelGroupGeometry.LocalPathOf(position), left, right, knownHash);
        }
        PbtNodeReader node = PbtNodeReader.FromValidated(encoding.Span);
        // The only stored leaf is the root of a single-leaf tree, whose hash is this group's identity.
        if (node.IsLeaf) return new(TKey.Create(node.Key), _groupHash);
        return new(encoding, PbtFourLevelGroupGeometry.LocalPathOf(position), knownHash);
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
    private void GetChildHashes(scoped in PbtTraversalPath path, int leftPosition, int rightPosition, out ValueHash256 left, out ValueHash256 right)
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
