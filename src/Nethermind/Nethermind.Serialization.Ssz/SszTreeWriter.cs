// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Serialization.Ssz;

/// <summary>Collects the hash trees of an SSZ value, and the hash of the value.</summary>
/// <remarks>
/// Every tree is written after the trees that it points to, and the root tree last, so the bytes of a tree and all trees below it are
/// one block. Offsets are relative to the tree that has them, so such a block can be copied as it is.
/// After a write throws, the output is unusable. Not thread-safe.
/// </remarks>
/// <param name="chunkSize">The minimum size of each array that the trees are held in, in bytes.</param>
public sealed class SszTreeWriter(int chunkSize) : IDisposable
{
    private const int NoTree = -1;

    private readonly SszChunks _bytes = new(chunkSize);
    private readonly SszNesting _nesting = new();

    /// <summary>The state of the open writers by depth, kept for later writers at that depth.</summary>
    private readonly List<Frame> _frames = [new()];

    private Frame Current => _frames[_nesting.Depth];

    /// <summary>The number of bytes written.</summary>
    public int Length => _bytes.Length;

    /// <summary>The hash of the value.</summary>
    public UInt256 Root { get; private set; }

    /// <summary>Where the tree of the value starts, or -1 when its type has no tree.</summary>
    public int RootStart { get; private set; } = NoTree;

    /// <summary>The written trees, valid until this writer is disposed or written to.</summary>
    /// <exception cref="InvalidOperationException">A writer is still open, so the trees are incomplete.</exception>
    public ReadOnlySequence<byte> ToSequence()
    {
        _nesting.EnsureInnermost(0, "the output");
        return _bytes.ToSequence();
    }

    /// <summary>Returns the arrays to the pool, which invalidates every sequence taken from this writer.</summary>
    public void Dispose()
    {
        _bytes.Dispose();
        _nesting.Reset();
    }

    /// <summary>Opens the writer of a container.</summary>
    /// <returns>The id of the writer.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public int OpenContainer(int memberCount)
    {
        int id = OpenBuffered();
        Current.SetLeafCount(memberCount);
        return id;
    }

    /// <summary>Checks that the member at <paramref name="index"/> is the next one for the writer <paramref name="id"/> to write.</summary>
    /// <param name="id">The id of the writer.</param>
    /// <param name="index">The index of the member in the order of writing.</param>
    /// <param name="leaf">The leaf that <see cref="EndMember"/> sets.</param>
    /// <param name="name">The name of the member.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void BeginMember(int id, int index, int leaf, string name)
    {
        _nesting.BeginMember(id, index, name);
        Current.PendingLeaf = leaf;
    }

    /// <summary>Sets the hash of the member or item that was begun, which has no tree.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void EndMember(in UInt256 root) => EndValue(in root, NoTree);

    /// <summary>Copies a tree and all trees below it as the member or item that was begun.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void CopyTree(in UInt256 root, SszTreeReader tree)
    {
        int start = _bytes.Length + tree.Start;
        _bytes.Write(tree.Data);
        EndValue(in root, start);
    }

    /// <summary>Writes the tree of a container, if all of its members are written.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void CloseContainer(int id, int memberCount, SszTreeShape shape, SszTreeReader source, ReadOnlySpan<byte> activeFields)
    {
        Frame frame = Current;
        if (!_nesting.Close(id, memberCount)) return;

        UInt256 root = WriteBuffered(frame, shape, source, out int start);
        if (!activeFields.IsEmpty) Merkle.MixInActiveFields(ref root, activeFields);
        EndValue(in root, start);
    }

    /// <summary>Opens the writer of a tree whose leaves are kept until it is closed, because trees below it are written first.</summary>
    internal int OpenBuffered()
    {
        int id = Open();
        Current.SetLeafCount(0);
        Current.InnerStarts.Clear();
        return id;
    }

    /// <summary>Opens the writer of a tree without trees below it, which is written as its leaves are added.</summary>
    internal int OpenStreaming(SszTreeShape shape, out Span<byte> leafCount)
    {
        int id = Open();
        Frame frame = Current;
        frame.Nodes.Reset(shape);
        frame.Start = _bytes.Length;
        frame.PartialLength = 0;
        leafCount = _bytes.Reserve(SszTreeShape.CountSize);
        return id;
    }

    /// <summary>Begins the next item of a buffered tree.</summary>
    internal void BeginItem(int id)
    {
        _nesting.AddItems(id, 1);
        Frame frame = Current;
        frame.PendingLeaf = frame.LeafCount;
        frame.SetLeafCount(frame.LeafCount + 1);
    }

    internal void AddLeaf(int id, in UInt256 leaf, SszTreeReader source)
    {
        _nesting.AddItems(id, 1);
        Current.Nodes.Add(in leaf, source, _bytes);
    }

    internal void CopyLeaves(int id, SszTreeReader source, int start, int count)
    {
        _nesting.AddItems(id, count);
        Current.Nodes.Copy(source, start, count, _bytes);
    }

    /// <summary>Copies items of a buffered tree.</summary>
    /// <param name="id">The id of the writer.</param>
    /// <param name="source">The tree to copy from.</param>
    /// <param name="start">The first item.</param>
    /// <param name="count">The number of items, at least one.</param>
    /// <param name="itemShape">The layout of the trees of the items, or <c>null</c> when they have none.</param>
    internal void CopyItems(int id, SszTreeReader source, int start, int count, SszTreeShape? itemShape)
    {
        _nesting.AddItems(id, count);
        Frame frame = Current;
        int firstLeaf = frame.LeafCount;
        frame.SetLeafCount(firstLeaf + count);
        for (int i = 0; i < count; i++) frame.Leaves[firstLeaf + i] = source[start + i];

        if (itemShape is not { } shape) return;

        int regionStart = source.InnerRegionStart(start, shape);
        int shift = _bytes.Length - regionStart;
        for (int i = 0; i < count; i++) frame.InnerStarts.Add(source.InnerStart(start + i) + shift);
        _bytes.Write(source.Data[regionStart..source.InnerEnd(start + count - 1, shape)]);
    }

    /// <summary>Adds items that are packed into chunks.</summary>
    internal void AddPacked(int id, int count, ReadOnlySpan<byte> items, SszTreeReader source)
    {
        _nesting.AddItems(id, count);
        AddBytes(items, source);
    }

    /// <summary>Adds items that are packed into chunks, copying the chunks that are the same in the source.</summary>
    /// <param name="id">The id of the writer.</param>
    /// <param name="count">The number of items.</param>
    /// <param name="sourceStart">Where the items start in the encoded source.</param>
    /// <param name="items">The encoded items.</param>
    /// <param name="source">The tree of the source.</param>
    internal void CopyPacked(int id, int count, long sourceStart, ReadOnlySpan<byte> items, SszTreeReader source)
    {
        _nesting.AddItems(id, count);
        int head = Math.Min(-Current.PartialLength & (SszTreeShape.NodeSize - 1), items.Length);
        if ((sourceStart + head) % SszTreeShape.NodeSize == 0)
        {
            AddBytes(items[..head], source);
            items = items[head..];
            int chunks = items.Length / SszTreeShape.NodeSize;
            Current.Nodes.Copy(source, (int)((sourceStart + head) / SszTreeShape.NodeSize), chunks, _bytes);
            items = items[(chunks * SszTreeShape.NodeSize)..];
        }

        AddBytes(items, source);
    }

    /// <summary>Ends a streaming tree.</summary>
    internal void CloseStreaming(int id, bool mixInLength, Span<byte> leafCount, SszTreeReader source)
    {
        Frame frame = Current;
        int count = _nesting.Written;
        if (!_nesting.Close(id)) return;

        if (frame.PartialLength != 0) AddPartial(frame, source);
        BinaryPrimitives.WriteInt32LittleEndian(leafCount, frame.Nodes.Count);
        UInt256 root = frame.Nodes.Finish();
        if (mixInLength) Merkle.MixIn(ref root, count);
        EndValue(in root, frame.Start);
    }

    /// <summary>Ends a buffered tree that is not of a container.</summary>
    internal void CloseBuffered(int id, bool mixInLength, SszTreeShape shape, SszTreeReader source)
    {
        Frame frame = Current;
        int count = _nesting.Written;
        if (!_nesting.Close(id)) return;

        UInt256 root = WriteBuffered(frame, shape, source, out int start);
        if (mixInLength) Merkle.MixIn(ref root, count);
        EndValue(in root, start);
    }

    private int Open()
    {
        int id = _nesting.Open();
        if (_nesting.Depth == _frames.Count) _frames.Add(new Frame());
        return id;
    }

    private UInt256 WriteBuffered(Frame frame, SszTreeShape shape, SszTreeReader source, out int start)
    {
        start = _bytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(_bytes.Reserve(SszTreeShape.CountSize), frame.LeafCount);
        frame.Nodes.Reset(shape);
        for (int i = 0; i < frame.LeafCount; i++) frame.Nodes.Add(in frame.Leaves[i], source, _bytes);
        foreach (int innerStart in frame.InnerStarts)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_bytes.Reserve(SszTreeShape.OffsetSize), start - innerStart);
        }

        return frame.Nodes.Finish();
    }

    /// <summary>Gives the hash of a value, and where its tree starts, to the leaf of the parent that waits for it.</summary>
    private void EndValue(in UInt256 root, int start)
    {
        if (_nesting.Depth == 0)
        {
            Root = root;
            RootStart = start;
            return;
        }

        Frame parent = Current;
        parent.Leaves[parent.PendingLeaf] = root;
        if (start != NoTree) parent.InnerStarts.Add(start);
    }

    private void AddBytes(ReadOnlySpan<byte> bytes, SszTreeReader source)
    {
        Frame frame = Current;
        if (frame.PartialLength != 0)
        {
            int count = Math.Min(SszTreeShape.NodeSize - frame.PartialLength, bytes.Length);
            bytes[..count].CopyTo(frame.Partial.AsSpan(frame.PartialLength));
            frame.PartialLength += count;
            bytes = bytes[count..];
            if (frame.PartialLength < SszTreeShape.NodeSize) return;
            AddPartial(frame, source);
        }

        foreach (ref readonly UInt256 chunk in MemoryMarshal.Cast<byte, UInt256>(bytes)) frame.Nodes.Add(in chunk, source, _bytes);

        bytes = bytes[(bytes.Length / SszTreeShape.NodeSize * SszTreeShape.NodeSize)..];
        bytes.CopyTo(frame.Partial);
        frame.PartialLength = bytes.Length;
    }

    private void AddPartial(Frame frame, SszTreeReader source)
    {
        frame.Partial.AsSpan(frame.PartialLength).Clear();
        frame.PartialLength = 0;
        frame.Nodes.Add(MemoryMarshal.Read<UInt256>(frame.Partial), source, _bytes);
    }

    private sealed class Frame
    {
        public readonly PostOrderTree Nodes = new();

        // Of a buffered tree.
        public readonly List<int> InnerStarts = [];
        public UInt256[] Leaves = [];
        public int LeafCount;
        public int PendingLeaf;

        // Of a streaming tree.
        public readonly byte[] Partial = new byte[SszTreeShape.NodeSize];
        public int PartialLength;
        public int Start;

        public void SetLeafCount(int count)
        {
            if (count > Leaves.Length) Array.Resize(ref Leaves, Math.Max(count, Leaves.Length * 2));
            LeafCount = count;
        }
    }
}
