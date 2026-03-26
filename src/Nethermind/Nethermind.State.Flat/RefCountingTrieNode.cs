// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A poolable, ref-counted trie node that delegates RLP storage to a type-specialized impl object
/// (<see cref="TrieNodeBranch"/>, <see cref="TrieNodeExtension"/>, or <see cref="TrieNodeLeaf"/>).
/// Created by <see cref="RefCountingTrieNodePool"/> and tracked by <see cref="RefCountingRlpNodePoolTracker"/>.
/// </summary>
public sealed class RefCountingTrieNode : RefCountingDisposable
{
    /// <summary>Weighted average of Branch(~808), Extension(~328), Leaf(~384) sizes.</summary>
    public const int EstimatedSize = 680;

    private RefCountingRlpNodePoolTracker _tracker = null!;
    private object _nodeImpl = null!;

    public ValueHash256 Hash;
    public NodeType NodeType;

    internal RefCountingTrieNode() : base(initialCount: 0) { }

    /// <summary>Binds this node to a tracker. Called by the pool before each use.</summary>
    internal void SetTracker(RefCountingRlpNodePoolTracker tracker) =>
        _tracker = tracker;

    /// <summary>
    /// Initializes this node with the given hash, node type, and impl object.
    /// After this call, the node has exactly one lease.
    /// </summary>
    internal void Initialize(ValueHash256 hash, NodeType nodeType, object impl)
    {
        Hash = hash;
        NodeType = nodeType;
        _nodeImpl = impl;
        _leases.Value = 1;
    }

    protected override void CleanUp()
    {
        Hash = default;
        _tracker.Return(this);
    }

    /// <summary>Tries to acquire a lease. Returns false if already disposed.</summary>
    public new bool TryAcquireLease() => base.TryAcquireLease();

    /// <summary>Current lease count. For diagnostics only.</summary>
    public long LeaseCount => Volatile.Read(ref _leases.Value);

    /// <summary>The type-specialized impl object for direct access in hot paths.</summary>
    internal object NodeImpl => _nodeImpl;

    /// <summary>Returns a read-only span over the valid RLP bytes.</summary>
    public ReadOnlySpan<byte> RlpSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => NodeType switch
        {
            NodeType.Branch => Unsafe.As<TrieNodeBranch>(_nodeImpl).AsSpan(),
            NodeType.Extension => Unsafe.As<TrieNodeExtension>(_nodeImpl).AsSpan(),
            _ => Unsafe.As<TrieNodeLeaf>(_nodeImpl).AsSpan(),
        };
    }

    /// <summary>Length of the valid RLP bytes.</summary>
    public int RlpLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => NodeType switch
        {
            NodeType.Branch => Unsafe.As<TrieNodeBranch>(_nodeImpl).Length,
            NodeType.Extension => Unsafe.As<TrieNodeExtension>(_nodeImpl).Length,
            _ => Unsafe.As<TrieNodeLeaf>(_nodeImpl).Length,
        };
    }

    /// <summary>Copies the valid RLP bytes into a new heap array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] RlpToArray() => NodeType switch
    {
        NodeType.Branch => Unsafe.As<TrieNodeBranch>(_nodeImpl).ToArray(),
        NodeType.Extension => Unsafe.As<TrieNodeExtension>(_nodeImpl).ToArray(),
        _ => Unsafe.As<TrieNodeLeaf>(_nodeImpl).ToArray(),
    };

    /// <summary>
    /// Returns the 32-byte hash of child at <paramref name="index"/> by reading from the RLP at the stored offset.
    /// Only valid for Branch nodes. Returns <c>null</c> if the child offset is 0 (empty/absent).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Hash256? GetChildHash(int index) =>
        Unsafe.As<TrieNodeBranch>(_nodeImpl).GetChildHash(index);

    /// <summary>
    /// Returns the raw RLP bytes of the child item at <paramref name="index"/>.
    /// Only valid for Branch nodes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetChildRlp(int index) =>
        Unsafe.As<TrieNodeBranch>(_nodeImpl).GetChildRlp(index);
}
