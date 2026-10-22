// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
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

/// <summary>The working memory of one bulk set on one thread: new nodes, their children, loaded records and commit scratch.</summary>
/// <remarks>
/// Nodes and children are addressed by index, as their lists grow by reallocation, so a reference into them does not survive
/// an add. All memory is pooled and returned on <see cref="Dispose"/>, dropping the references to entry values and records.
/// </remarks>
internal sealed class BulkSetWorkingState : IDisposable
{
    public const int BranchChildCount = 16;
    public const int MaxKeyNibbles = 64;

    private const int InitialNodeCapacity = 64;

    private static readonly ChildRef[] EmptyChildren = new ChildRef[BranchChildCount];

    private readonly ArrayPoolList<PendingNode> _nodes = new(InitialNodeCapacity);
    private readonly NativeMemoryList<ChildRef> _children = new(InitialNodeCapacity * BranchChildCount);
    private readonly ArrayPoolList<SetOneFrame> _frames = new(MaxKeyNibbles, MaxKeyNibbles);

    private NativeMemoryList<int> _collected = NativeMemoryList<int>.Empty();
    private NativeMemoryList<int> _order = NativeMemoryList<int>.Empty();
    private NativeMemoryList<int> _hashed = NativeMemoryList<int>.Empty();
    private NativeMemoryList<int> _recordStarts = NativeMemoryList<int>.Empty();
    private NativeMemoryList<(int Start, int Length)> _messages = NativeMemoryList<(int Start, int Length)>.Empty();
    private NativeMemoryList<ValueHash256> _hashes = NativeMemoryList<ValueHash256>.Empty();

    /// <summary>The nodes loaded from the store, which <see cref="ClearPending"/> keeps.</summary>
    public LoadedRecordBuffer Loaded { get; } = new();

    public Span<PendingNode> Nodes => _nodes.AsSpan();
    public int NodeCount => _nodes.Count;
    public Span<ChildRef> Children => _children.AsSpan();

    /// <summary>The traverse stack of a single-entry set, which never nests in another on the same working state.</summary>
    public Span<SetOneFrame> Frames => _frames.AsSpan();

    public Span<int> Collected => _collected.AsSpan();
    public Span<int> Order => _order.AsSpan();
    public Span<int> Hashed => _hashed.AsSpan();
    public Span<int> RecordStarts => _recordStarts.AsSpan();
    public Span<(int Start, int Length)> Messages => _messages.AsSpan();
    public Span<ValueHash256> Hashes => _hashes.AsSpan();

    /// <returns>The index of sixteen empty children.</returns>
    public int ReserveChildSpace()
    {
        int start = _children.Count;
        _children.AddRange(EmptyChildren);
        return start;
    }

    public int AddPendingLeaf(ReadOnlySpan<byte> key, ReadOnlyMemory<byte> value)
    {
        int index = AddPending(PendingKind.Leaf, key);
        Nodes[index].Value = value;
        return index;
    }

    public int AddPendingExtension(ReadOnlySpan<byte> key, in ChildRef child)
    {
        int index = AddPending(PendingKind.Extension, key);
        Nodes[index].ExtensionChild = child;
        return index;
    }

    public int AddPendingBranch(int childrenStart)
    {
        int index = AddPending(PendingKind.Branch, []);
        Nodes[index].ChildrenStart = childrenStart;
        return index;
    }

    /// <summary>Sizes the commit scratch for <paramref name="count"/> nodes.</summary>
    public void EnsureCommitCapacity(int count)
    {
        if (_collected.Count >= count) return;

        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)count);
        Reallocate(ref _collected, capacity);
        Reallocate(ref _order, capacity);
        Reallocate(ref _hashed, capacity);
        Reallocate(ref _recordStarts, capacity);
        Reallocate(ref _messages, capacity);
        Reallocate(ref _hashes, capacity);
    }

    /// <summary>Drops the pending nodes and their children for a reuse in the same traversal.</summary>
    public void ClearPending()
    {
        _nodes.Clear();
        _children.Clear();
    }

    public void Dispose()
    {
        Loaded.Dispose();
        _nodes.Dispose();
        _children.Dispose();
        _frames.Dispose();
        _collected.Dispose();
        _order.Dispose();
        _hashed.Dispose();
        _recordStarts.Dispose();
        _messages.Dispose();
        _hashes.Dispose();
    }

    private static void Reallocate<T>(ref NativeMemoryList<T> list, int count) where T : unmanaged
    {
        list.Dispose();
        list = new NativeMemoryList<T>(count, count);
    }

    private int AddPending(PendingKind kind, ReadOnlySpan<byte> key)
    {
        int index = _nodes.Count;
        _nodes.Add(new PendingNode { Kind = kind, KeyLength = (byte)key.Length });
        key.CopyTo(Nodes[index].Key);
        return index;
    }
}
