// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal struct GroupFrameReader<TKey, TPath> : IDisposable
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    private readonly IPbtStore _store;
    private readonly ValueHash256 _groupHash;
    private readonly TrieUpdaterMetrics? _metrics;
    private RefCountingMemory? _lease;
    private bool _loaded;
    private OffsetBuffer _offsets;
    private LengthBuffer _lengths;
    private HashBuffer _hashes;
    private uint _hashed;
    internal uint Taken;

    internal GroupFrameReader(IPbtStore store, TPath groupKey, in ValueHash256 groupHash, TrieUpdaterMetrics? metrics)
    {
        GroupKey = groupKey;
        _groupHash = groupHash;
        _store = store;
        _metrics = metrics;
        metrics?.IncrementGroupFrameResolutions();
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _metrics?.IncrementPhysicalGroupFetches();
        PbtTraversalPath path = PbtTraversalPath.FromPath(stackalloc byte[PbtStorageFullKey.MaxLength], GroupKey);
        _lease = _store.GetNodeGroup(path, _groupHash);
        _loaded = true;
        if (_lease is null) return;
        try
        {
            _metrics?.IncrementGroupParses();
            PbtNodeGroupReader reader = new(path, _lease.GetSpan());
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

    internal TPath GroupKey { get; }
    internal int BitDepth => GroupKey.BitDepth;

    internal ReadOnlyMemory<byte> GetEncoding(int position)
    {
        EnsureLoaded();
        return _lengths[position] == 0 ? default : _lease!.Memory.Slice(_offsets[position], _lengths[position]);
    }

    internal int CopyRange(PbtNodeGroupWriter<TPath> writer, int startPosition, int endPosition)
    {
        if (startPosition == endPosition) return 0;
        EnsureLoaded();
        while (startPosition < endPosition && _lengths[startPosition] == 0) startPosition++;
        if (startPosition == endPosition) return 0;
        int lastPosition = endPosition - 1;
        while (_lengths[lastPosition] == 0) lastPosition--;
        int startOffset = _offsets[startPosition];
        ReadOnlySpan<byte> entries = _lease!.GetSpan().Slice(startOffset,
            _offsets[lastPosition] + _lengths[lastPosition] - startOffset);
        return writer.CopyRange(entries, _offsets, _lengths, startPosition, lastPosition);
    }

    internal TrieUpdater<TKey, TPath>.Subtree Acquire(int position)
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(position);
        if (encoding.IsEmpty)
        {
            int width = PbtFourLevelGroupGeometry.WidthOf(position);
            if (width is 1 or PbtFourLevelGroupGeometry.BoundarySlots) return default;
            ValueHash256 left = GetHash(position - width);
            ValueHash256 right = GetHash(position - 1);
            return left == default || right == default ? default : new(PbtFourLevelGroupGeometry.PathOf(GroupKey, position), left, right);
        }
        TPath? path = PbtNodeReader.FromValidated(encoding.Span).IsLeaf ? null : PbtFourLevelGroupGeometry.PathOf(GroupKey, position);
        return new(encoding, path);
    }

    private ValueHash256 GetHash(int position)
    {
        uint bit = 1u << position;
        if ((_hashed & bit) != 0) return _hashes[position];
        ReadOnlyMemory<byte> encoding = GetEncoding(position);
        ValueHash256 hash = default;
        if (!encoding.IsEmpty)
            hash = PbtNodeCodec.Hash(PbtNodeReader.FromValidated(encoding.Span));
        else if (PbtFourLevelGroupGeometry.WidthOf(position) is int width and > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
        {
            ValueHash256 left = GetHash(position - width);
            ValueHash256 right = GetHash(position - 1);
            if (left != default && right != default)
            {
                Span<byte> branch = stackalloc byte[67];
                PbtNodeCodec.CreateBranchEncoding(branch, 0, left, right);
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

    internal TrieUpdater<TKey, TPath>.Subtree Take(PbtNodeGroupWriter<TPath> writer, int position, bool allowAbsent = false)
    {
        Debug.Assert(position > writer.LastPosition, "Cannot take a PBT node after its output position has passed.");
        TrieUpdater<TKey, TPath>.Subtree node = (Taken & (1U << position)) == 0
            ? Acquire(position)
            : default;
        if (node.IsEmpty && !allowAbsent) throw new InvalidDataException("A referenced PBT node is missing.");
        Taken |= 1U << position;
        return node;
    }

}
