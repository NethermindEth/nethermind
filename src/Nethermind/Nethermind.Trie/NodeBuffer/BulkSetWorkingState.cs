// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

internal enum PendingKind : byte
{
    Leaf,
    Extension,
    Branch,
}

/// <summary>Key nibbles, one per byte.</summary>
[InlineArray(BulkSetWorkingState.MaxKeyNibbles)]
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
    /// <summary>An entry's managed value or a leaf value borrowed for the enclosing buffer operation.</summary>
    public ReadOnlyMemory<byte> Value;

    /// <summary>Index of a branch's sixteen children in <see cref="BulkSetWorkingState.Children"/>.</summary>
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
internal readonly struct SetOneFrame(NodeRecord record, byte skip, byte childNibble)
{
    public readonly NodeRecord Record = record;
    public readonly byte Skip = skip;
    public readonly byte ChildNibble = childNibble;
}

/// <summary>The working memory of one bulk set on one thread: new nodes, their children and commit scratch.</summary>
/// <remarks>
/// Nodes and children are addressed by index, as their arrays grow by reallocation, so a reference into them does not survive
/// an add. Working states are pooled, unless grown past <see cref="MaxPooledNodeCapacity"/> nodes; a returned one drops its references
/// to entry values and records.
/// </remarks>
internal sealed class BulkSetWorkingState
{
    public const int BranchChildCount = 16;
    public const int MaxKeyNibbles = 64;

    private const int MaxPooled = 64;
    private const int InitialNodeCapacity = 64;
    private const int MaxPooledNodeCapacity = 4096;

    private static readonly ConcurrentQueue<BulkSetWorkingState> Pool = new();

    public PendingNode[] Nodes = new PendingNode[InitialNodeCapacity];
    public int NodeCount;
    public ChildRef[] Children = new ChildRef[InitialNodeCapacity * BranchChildCount];
    public int ChildCount;

    /// <summary>The traverse stack of a single-entry set, which never nests in another on the same working state.</summary>
    public readonly SetOneFrame[] Frames = new SetOneFrame[MaxKeyNibbles];

    public int[] Collected = [];
    public int[] Order = [];
    public int[] Hashed = [];
    public int[] RecordStarts = [];
    public (int Start, int Length)[] Messages = [];
    public ValueHash256[] Hashes = [];

    public static BulkSetWorkingState Rent() => Pool.TryDequeue(out BulkSetWorkingState? workingState) ? workingState : new BulkSetWorkingState();

    public static void Return(BulkSetWorkingState workingState)
    {
        Array.Clear(workingState.Nodes, 0, workingState.NodeCount);
        Array.Clear(workingState.Frames);
        workingState.NodeCount = 0;
        workingState.ChildCount = 0;
        if (Pool.Count < MaxPooled && workingState.Nodes.Length <= MaxPooledNodeCapacity && workingState.Children.Length <= MaxPooledNodeCapacity * BranchChildCount)
        {
            Pool.Enqueue(workingState);
        }
    }

    /// <returns>The index of sixteen empty children.</returns>
    public int ReserveChildSpace()
    {
        if (ChildCount + BranchChildCount > Children.Length) Array.Resize(ref Children, Children.Length * 2);

        int start = ChildCount;
        Children.AsSpan(start, BranchChildCount).Clear();
        ChildCount += BranchChildCount;
        return start;
    }

    public int AddPendingLeaf(ReadOnlySpan<byte> key, ReadOnlyMemory<byte> value)
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
