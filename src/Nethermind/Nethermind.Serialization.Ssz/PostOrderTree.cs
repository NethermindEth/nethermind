// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz;

/// <summary>Hashes leaves that are added in order, and writes the nodes of their tree in post-order.</summary>
/// <remarks>A node whose children are both unchanged from a source tree is copied from it.</remarks>
internal sealed class PostOrderTree
{
    /// <summary>By level, the last complete node that waits for its right sibling.</summary>
    private readonly UInt256[] _pending = new UInt256[32];
    private readonly UInt256[] _subtreeRoots = new UInt256[16];
    private SszTreeShape _shape;

    /// <summary>By level, whether the pending node equals the one at the same place in the source.</summary>
    private uint _unchanged;
    private int _subtree;
    private int _local;

    /// <summary>The number of leaves.</summary>
    public int Count { get; private set; }

    public void Reset(SszTreeShape shape)
    {
        _shape = shape;
        _unchanged = 0;
        _subtree = _local = Count = 0;
    }

    public void Add(in UInt256 leaf, SszTreeReader source, SszChunks output)
    {
        WriteNode(in leaf, output);
        Append(leaf, 0, Count < source.LeafCount && source[Count] == leaf, source, output);
    }

    /// <summary>Adds <paramref name="count"/> leaves of <paramref name="source"/>, with the nodes between them where their alignment allows.</summary>
    public void Copy(SszTreeReader source, int start, int count, SszChunks output)
    {
        while (count > 0)
        {
            (int subtree, int local) = _shape.Locate(start);
            int maxLevel = _shape.IsProgressive ? 2 * Math.Min(subtree, _subtree) : Math.Max(_shape.Depth - 1, 0);
            int alignment = Math.Min(BitOperations.TrailingZeroCount(local), BitOperations.TrailingZeroCount(_local));
            int level = Math.Min(Math.Min(alignment, maxLevel), BitOperations.Log2((uint)count));

            output.Write(source.Run(start, level));
            Append(source.Node(start, level), level, start == Count, source, output);
            start += 1 << level;
            count -= 1 << level;
        }
    }

    /// <summary>The root of the leaves with the padding of the shape, without any mix-in.</summary>
    public UInt256 Finish()
    {
        if (!_shape.IsProgressive) return FinishSubtree(_shape.Depth);

        // EIP-7916: every subtree is the left sibling of the rest, which ends in a zero chunk.
        UInt256 root = _local == 0 ? UInt256.Zero : Merkle.HashConcatenation(FinishSubtree(2 * _subtree), UInt256.Zero, 0);
        for (int i = _subtree - 1; i >= 0; i--)
        {
            root = Merkle.HashConcatenation(_subtreeRoots[i], root, 0);
        }

        return root;
    }

    /// <param name="node">A node above <c>2^level</c> leaves that are next in order, already written.</param>
    private void Append(UInt256 node, int level, bool unchanged, SszTreeReader source, SszChunks output)
    {
        int first = Count;
        int local = _local;
        Count += 1 << level;
        _local += 1 << level;

        for (; (local >> level & 1) != 0; level++)
        {
            first -= 1 << level;
            local -= 1 << level;
            unchanged &= (_unchanged >> level & 1) != 0;
            bool isStored = _shape.IsProgressive || level + 1 < _shape.Depth;
            node = unchanged && isStored ? source.Node(first, level + 1) : Merkle.HashConcatenation(_pending[level], node, level);
            if (isStored) WriteNode(in node, output);
        }

        _pending[level] = node;
        _unchanged = unchanged ? _unchanged | 1u << level : _unchanged & ~(1u << level);

        if (_shape.IsProgressive && _local == 1 << (2 * _subtree))
        {
            _subtreeRoots[_subtree++] = node;
            _local = 0;
        }
    }

    private UInt256 FinishSubtree(int depth)
    {
        if (_local == 0) return Merkle.ZeroHash(depth);

        int level = BitOperations.TrailingZeroCount(_local);
        UInt256 node = _pending[level];
        for (; level < depth; level++)
        {
            node = level < 31 && (_local - 1 >> level & 1) != 0
                ? Merkle.HashConcatenation(_pending[level], node, level)
                : Merkle.HashConcatenation(node, Merkle.ZeroHash(level), level);
        }

        return node;
    }

    private static void WriteNode(in UInt256 node, SszChunks output) =>
        MemoryMarshal.Write(output.Reserve(SszTreeShape.NodeSize), in node);
}
