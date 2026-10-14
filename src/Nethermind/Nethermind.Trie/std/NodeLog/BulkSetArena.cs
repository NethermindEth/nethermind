// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

internal enum PendingKind : byte
{
    Leaf,
    Extension,
    Branch,
}

/// <summary>A byte array slice: an entry's value or a leaf value inside a record.</summary>
internal readonly struct ValueRef(byte[] array, int offset, int length)
{
    private readonly byte[] _array = array;
    private readonly int _offset = offset;
    private readonly int _length = length;

    public ReadOnlySpan<byte> Span => _array.AsSpan(_offset, _length);
}

/// <summary>Key nibbles, one per byte.</summary>
[InlineArray(BulkSetArena.MaxKeyNibbles)]
internal struct NibbleKey
{
    private byte _element0;
}

/// <summary>A new node a bulk set has not encoded yet.</summary>
internal struct PendingNode
{
    public PendingKind Kind;
    public byte KeyLength;
    public NibbleKey Key;
    public ValueRef Value;

    /// <summary>Index of a branch's sixteen children in <see cref="BulkSetArena.Children"/>.</summary>
    public int ChildrenStart;

    public ChildRef ExtensionChild;

    /// <summary>Set while committing, as are <see cref="Parent"/> and <see cref="Slot"/>.</summary>
    public TreePath Path;

    public int Parent;
    public byte Slot;

    [UnscopedRef]
    public ReadOnlySpan<byte> KeyNibbles => ((ReadOnlySpan<byte>)Key)[..KeyLength];
}

/// <summary>A branch or extension record a single-entry set walked through, the extension less the first <see cref="Skip"/> key nibbles.</summary>
internal readonly struct SetOneFrame(NodeRecordRef record, byte skip, byte childNibble)
{
    public readonly NodeRecordRef Record = record;
    public readonly byte Skip = skip;
    public readonly byte ChildNibble = childNibble;
}

/// <summary>The working memory of one bulk set on one thread: new nodes, their children and commit scratch.</summary>
/// <remarks>
/// Nodes and children are addressed by index, as their arrays grow by reallocation, so a reference into them does not survive
/// an add. Arenas are pooled; a returned arena drops its references to entry values and records.
/// </remarks>
internal sealed class BulkSetArena
{
    public const int BranchChildCount = 16;
    public const int MaxKeyNibbles = 64;

    private const int MaxPooled = 64;
    private const int InitialNodeCapacity = 64;

    private static readonly ConcurrentQueue<BulkSetArena> Pool = new();

    public PendingNode[] Nodes = new PendingNode[InitialNodeCapacity];
    public int NodeCount;
    public ChildRef[] Children = new ChildRef[InitialNodeCapacity * BranchChildCount];
    public int ChildCount;

    /// <summary>The traverse stack of a single-entry set, which never nests in another on the same arena.</summary>
    public readonly SetOneFrame[] Frames = new SetOneFrame[MaxKeyNibbles];

    public int[] Collected = [];
    public int[] Order = [];
    public int[] Hashed = [];
    public int[] RecordStarts = [];
    public (int Start, int Length)[] Messages = [];
    public ValueHash256[] Hashes = [];

    public long NodesFromLog;
    public long NodesFromSource;

    public static BulkSetArena Rent() => Pool.TryDequeue(out BulkSetArena? arena) ? arena : new BulkSetArena();

    public static void Return(BulkSetArena arena)
    {
        Array.Clear(arena.Nodes, 0, arena.NodeCount);
        Array.Clear(arena.Frames);
        arena.NodeCount = 0;
        arena.ChildCount = 0;
        arena.NodesFromLog = 0;
        arena.NodesFromSource = 0;
        if (Pool.Count < MaxPooled) Pool.Enqueue(arena);
    }

    /// <returns>The index of sixteen empty children.</returns>
    public int AddChildren()
    {
        if (ChildCount + BranchChildCount > Children.Length) Array.Resize(ref Children, Children.Length * 2);

        int start = ChildCount;
        Children.AsSpan(start, BranchChildCount).Clear();
        ChildCount += BranchChildCount;
        return start;
    }

    public int AddLeaf(ReadOnlySpan<byte> key, in ValueRef value)
    {
        int index = Add(PendingKind.Leaf, key);
        Nodes[index].Value = value;
        return index;
    }

    public int AddExtension(ReadOnlySpan<byte> key, in ChildRef child)
    {
        int index = Add(PendingKind.Extension, key);
        Nodes[index].ExtensionChild = child;
        return index;
    }

    public int AddBranch(int childrenStart)
    {
        int index = Add(PendingKind.Branch, []);
        Nodes[index].ChildrenStart = childrenStart;
        return index;
    }

    /// <summary>Sizes the commit scratch for <paramref name="count"/> nodes.</summary>
    public void EnsureCommitCapacity(int count)
    {
        if (Collected.Length >= count) return;

        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)count);
        Collected = new int[capacity];
        Order = new int[capacity];
        Hashed = new int[capacity];
        RecordStarts = new int[capacity];
        Messages = new (int Start, int Length)[capacity];
        Hashes = new ValueHash256[capacity];
    }

    private int Add(PendingKind kind, ReadOnlySpan<byte> key)
    {
        if (NodeCount == Nodes.Length) Array.Resize(ref Nodes, Nodes.Length * 2);

        int index = NodeCount++;
        ref PendingNode node = ref Nodes[index];
        node = new PendingNode { Kind = kind, KeyLength = (byte)key.Length };
        key.CopyTo(node.Key);
        return index;
    }
}
