// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal struct GroupFrameReader<TKey, TPath> : IDisposable
    where TKey : struct, IPbtKey<TKey>
    where TPath : class, IPbtNodePath<TPath>
{
    private readonly RefCountingMemory? _lease;
    private readonly OffsetBuffer _offsets;
    private readonly LengthBuffer _lengths;
    private HashBuffer _hashes;
    private uint _hashed;
    internal uint Taken;

    internal GroupFrameReader(IPbtStore store, TPath groupKey, TrieUpdaterMetrics? metrics)
    {
        GroupKey = groupKey;
        metrics?.IncrementGroupFrameResolutions();
        metrics?.IncrementPhysicalGroupFetches();
        _lease = store.GetNodeGroup(groupKey);
        if (_lease is null) return;
        try
        {
            metrics?.IncrementGroupParses();
            PbtNodeGroupReader reader = new(groupKey, _lease.GetSpan());
            for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
            {
                if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
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

    internal ReadOnlyMemory<byte> GetEncoding(int position) => _lengths[position] == 0
        ? default
        : _lease!.Memory.Slice(_offsets[position], _lengths[position]);

    internal int CopyRange(PbtNodeGroupWriter writer, int startPosition, int endPosition)
    {
        while (startPosition < endPosition && _lengths[startPosition] == 0) startPosition++;
        if (startPosition == endPosition) return 0;
        int lastPosition = endPosition - 1;
        while (_lengths[lastPosition] == 0) lastPosition--;
        int startOffset = _offsets[startPosition];
        ReadOnlySpan<byte> entries = _lease!.GetSpan().Slice(startOffset,
            _offsets[lastPosition] + _lengths[lastPosition] - startOffset);
        return writer.CopyRange(entries, _offsets, _lengths, startPosition, lastPosition);
    }

    internal TrieUpdater<TKey, TPath>.Subtree Acquire(int position, TPath path)
    {
        ReadOnlyMemory<byte> encoding = GetEncoding(position);
        if (encoding.IsEmpty)
        {
            int width = PbtFourLevelGroupGeometry.WidthOf(position);
            if (width is 1 or PbtFourLevelGroupGeometry.BoundarySlots) return default;
            ValueHash256 left = GetHash(position - width);
            ValueHash256 right = GetHash(position - 1);
            return left == default || right == default ? default : new(path, left, right);
        }
        _lease!.AcquireLease();
        try { return new(_lease, encoding, path); }
        catch
        {
            ((IDisposable)_lease).Dispose();
            throw;
        }
    }

    private ValueHash256 GetHash(int position)
    {
        uint bit = 1u << position;
        if ((_hashed & bit) != 0) return _hashes[position];
        ReadOnlyMemory<byte> encoding = GetEncoding(position);
        ValueHash256 hash = default;
        if (!encoding.IsEmpty)
            hash = PbtNodeCodec.Hash(new PbtNodeReader(encoding.Span));
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

    internal int Position(TPath path)
    {
        int completeBytes = BitDepth >> 3;
        int remainingBits = BitDepth & 7;
        if (PbtFourLevelGroupGeometry.GroupDepthOf(path.BitDepth) != BitDepth
            || !path.Path[..completeBytes].SequenceEqual(GroupKey.Path[..completeBytes])
            || (remainingBits != 0 && ((path.Path[completeBytes] ^ GroupKey.Path[completeBytes]) & 0xF0) != 0))
            throw new InvalidOperationException("The PBT node does not belong to the active group.");
        return PbtFourLevelGroupGeometry.PositionOf(path);
    }

    public void Dispose() => ((IDisposable?)_lease)?.Dispose();

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

    internal TrieUpdater<TKey, TPath>.Subtree Take(PbtNodeGroupWriter writer, TPath path, bool allowAbsent = false)
    {
        int position = Position(path);
        if (position < writer.NextPosition) throw new InvalidOperationException("Cannot take a PBT node after its output position has passed.");
        TrieUpdater<TKey, TPath>.Subtree node = (Taken & (1U << position)) == 0
            ? Acquire(position, path)
            : default;
        if (node.IsEmpty && !allowAbsent) throw new InvalidDataException("A referenced PBT node is missing.");
        Taken |= 1U << position;
        return node;
    }

    internal void Resolve(PbtNodeGroupWriter writer, ref TrieUpdater<TKey, TPath>.Subtree subtree)
    {
        if (subtree.IsReference)
            subtree = Take(writer, subtree.Path!);
    }
}
