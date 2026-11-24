// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz;

/// <summary>The layout of a hash tree, which depends on its SSZ type and its number of leaves.</summary>
/// <remarks>
/// A tree is encoded as its leaf count, its nodes in post-order and the offsets of its inner trees. Only nodes whose leaves are all
/// present are stored: those above them have zero padding below and change with every appended leaf. The node at the full depth is
/// not stored either, because the leaf that points to the tree has its hash. A progressive tree is a sequence of subtrees with 1, 4,
/// 16, ... leaves (EIP-7916), without the nodes that chain them.
/// </remarks>
/// <param name="depth">The depth of the tree, not used for a progressive one.</param>
/// <param name="progressive">Whether the tree is progressive.</param>
/// <param name="innerTrees">The number of inner trees, or <see cref="PerLeaf"/>.</param>
public readonly struct SszTreeShape(int depth, bool progressive, int innerTrees)
{
    /// <summary>The number of inner trees of a tree whose every leaf has one.</summary>
    public const int PerLeaf = -1;

    internal const int CountSize = sizeof(uint);
    internal const int NodeSize = 32;
    internal const int OffsetSize = sizeof(uint);

    internal int Depth => depth;

    internal bool IsProgressive => progressive;

    internal int InnerTrees(int leafCount) => innerTrees == PerLeaf ? leafCount : innerTrees;

    /// <summary>The encoded length of a tree.</summary>
    public int Length(int leafCount) => checked(CountSize + NodeCount(leafCount) * NodeSize + InnerTrees(leafCount) * OffsetSize);

    internal int NodeCount(int leafCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leafCount);
        bool isFull = !progressive && depth is > 0 and < 31 && leafCount == 1 << depth;
        return NodeIndex(leafCount) - (isFull ? 1 : 0);
    }

    /// <summary>The index of the node that is the leaf at <paramref name="leaf"/>, which is where its aligned runs start too.</summary>
    internal int NodeIndex(int leaf)
    {
        (int subtree, int local) = Locate(leaf);
        int previousSubtrees = (int)(2 * ((1L << (2 * subtree)) - 1) / 3) - subtree;
        return checked(previousSubtrees + 2 * local - BitOperations.PopCount((uint)local));
    }

    /// <summary>The progressive subtree that has the leaf at <paramref name="leaf"/>, and where in it.</summary>
    internal (int Subtree, int Local) Locate(int leaf)
    {
        if (!progressive) return (0, leaf);
        int subtree = BitOperations.Log2(3UL * (uint)leaf + 1) >> 1;
        return (subtree, (int)(leaf - ((1L << (2 * subtree)) - 1) / 3));
    }
}

/// <summary>Reads one tree of a hash tree blob, in which every tree comes after the trees that it points to.</summary>
public readonly ref struct SszTreeReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly SszTreeShape _shape;

    /// <param name="data">The blob, starting at the first tree below this one, or earlier.</param>
    /// <param name="start">Where this tree starts.</param>
    /// <param name="shape">The layout of this tree.</param>
    public SszTreeReader(ReadOnlySpan<byte> data, int start, SszTreeShape shape)
    {
        LeafCount = BinaryPrimitives.ReadInt32LittleEndian(data[start..]);
        Start = start;
        _shape = shape;
        _data = data[..checked(start + shape.Length(LeafCount))];
    }

    /// <summary>Reads the last tree of <paramref name="data"/>, which has a known number of leaves.</summary>
    public static SszTreeReader Last(ReadOnlySpan<byte> data, int leafCount, SszTreeShape shape) =>
        new(data, data.Length - shape.Length(leafCount), shape);

    /// <summary>Whether there is a tree; <c>default</c> stands for none.</summary>
    public bool Exists => !_data.IsEmpty;

    /// <summary>The number of leaves that the value has, without padding.</summary>
    public int LeafCount { get; }

    /// <summary>The hash of the leaf at <paramref name="leaf"/>.</summary>
    public UInt256 this[int leaf]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(leaf);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(leaf, LeafCount);
            return Node(leaf, 0);
        }
    }

    /// <summary>Reads the inner tree at <paramref name="index"/>.</summary>
    /// <param name="index">The index of the inner tree.</param>
    /// <param name="previous">The layout of the inner tree before it, which is not used for the first one.</param>
    /// <param name="shape">The layout of the inner tree.</param>
    public SszTreeReader Inner(int index, SszTreeShape previous, SszTreeShape shape)
    {
        int regionStart = InnerRegionStart(index, previous);
        return new SszTreeReader(_data[regionStart..], InnerStart(index) - regionStart, shape);
    }

    internal int Start { get; }

    /// <summary>This tree and all trees below it.</summary>
    internal ReadOnlySpan<byte> Data => _data;

    /// <summary>The node above <c>2^level</c> leaves, from the one at <paramref name="first"/>.</summary>
    internal UInt256 Node(int first, int level) => MemoryMarshal.Read<UInt256>(Run(first, level)[^SszTreeShape.NodeSize..]);

    /// <summary>The nodes of the aligned run of <c>2^level</c> leaves from the one at <paramref name="first"/>.</summary>
    internal ReadOnlySpan<byte> Run(int first, int level) =>
        _data.Slice(Start + SszTreeShape.CountSize + _shape.NodeIndex(first) * SszTreeShape.NodeSize, ((2 << level) - 1) * SszTreeShape.NodeSize);

    internal int InnerStart(int index)
    {
        int count = _shape.InnerTrees(LeafCount);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        return Start - BinaryPrimitives.ReadInt32LittleEndian(_data[^((count - index) * SszTreeShape.OffsetSize)..]);
    }

    /// <summary>Where the trees below the inner tree at <paramref name="index"/> start, which is where the previous inner tree ends.</summary>
    internal int InnerRegionStart(int index, SszTreeShape previous) => index == 0 ? 0 : InnerEnd(index - 1, previous);

    internal int InnerEnd(int index, SszTreeShape shape)
    {
        int start = InnerStart(index);
        return checked(start + shape.Length(BinaryPrimitives.ReadInt32LittleEndian(_data[start..])));
    }
}

/// <summary>Reads the tree of an inner value.</summary>
public delegate T SszItemTreeReader<T>(SszTreeReader tree) where T : allows ref struct;

/// <summary>Reads the tree of a collection whose every item has a tree.</summary>
/// <typeparam name="T">Reads the tree of an item.</typeparam>
public readonly ref struct SszListTreeReader<T>(SszTreeReader tree, SszTreeShape itemShape, SszItemTreeReader<T> readItem) where T : allows ref struct
{
    private readonly SszTreeReader _tree = tree;

    /// <summary>The number of items.</summary>
    public int Count => _tree.LeafCount;

    /// <summary>Reads the tree of the item at <paramref name="index"/>.</summary>
    public T this[int index] => readItem(_tree.Inner(index, itemShape, itemShape));

    /// <summary>The hash of the item at <paramref name="index"/>.</summary>
    public UInt256 Root(int index) => _tree[index];
}
