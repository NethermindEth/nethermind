// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A poolable, ref-counted trie node. RLP is stored in a <see cref="CappedArray{T}"/> backed by a pooled byte[]
/// of size appropriate to the node type (544 for branch, 96 for extension, 160 for leaf).
/// </summary>
public sealed class RefCountingTrieNode : RefCountingDisposable
{
    public const int EstimatedSize = 680;

    private RefCountingRlpNodePoolTracker _tracker = null!;

    public ValueHash256 Hash;
    public NodeType NodeType;

    /// <summary>RLP data backed by a pooled byte[] of type-specific size.</summary>
    public CappedArray<byte> Rlp;

    /// <summary>
    /// Pre-parsed child offsets. Branch uses all 17; extension uses index 0; leaf unused.
    /// </summary>
    public ChildOffsetBuffer ChildOffsets;

    internal RefCountingTrieNode() : base(initialCount: 0) { }

    /// <summary>Binds this node to a tracker. Called by the pool before each use.</summary>
    internal void SetTracker(RefCountingRlpNodePoolTracker tracker) =>
        _tracker = tracker;

    /// <summary>
    /// Initializes this node. After this call, the node has exactly one lease.
    /// </summary>
    internal void Initialize(ValueHash256 hash, NodeType nodeType, CappedArray<byte> rlp)
    {
        Hash = hash;
        NodeType = nodeType;
        Rlp = rlp;
        _leases.Value = 1;
    }

    protected override void CleanUp()
    {
        Hash = default;
        ChildOffsets = default;
        _tracker.Return(this);
    }

    /// <summary>Tries to acquire a lease. Returns false if already disposed.</summary>
    public new bool TryAcquireLease() => base.TryAcquireLease();

    /// <summary>Current lease count. For diagnostics only.</summary>
    public long LeaseCount => Volatile.Read(ref _leases.Value);

    /// <summary>Returns a read-only span over the valid RLP bytes.</summary>
    public ReadOnlySpan<byte> RlpSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Rlp.AsSpan();
    }

    /// <summary>Length of the valid RLP bytes.</summary>
    public int RlpLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Rlp.Length;
    }

    /// <summary>Copies the valid RLP bytes into a new heap array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] RlpToArray() => Rlp.ToArray()!;

    /// <summary>
    /// Returns the 32-byte hash of child at <paramref name="index"/> by reading from the RLP at the stored offset.
    /// Only valid for Branch nodes. Returns <c>null</c> if the child offset is 0 (empty/absent).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Hash256? GetChildHash(int index)
    {
        short offset = ChildOffsets[index];
        if (offset == 0) return null;

        ReadOnlySpan<byte> rlp = RlpSpan;
        if (offset < rlp.Length && rlp[offset] == 0xA0 && offset + 33 <= rlp.Length)
        {
            return new Hash256(rlp.Slice(offset + 1, 32));
        }

        return null;
    }

    /// <summary>
    /// Returns the raw RLP bytes of the child item at <paramref name="index"/>.
    /// Only valid for Branch nodes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetChildRlp(int index)
    {
        short offset = ChildOffsets[index];
        if (offset == 0) return ReadOnlySpan<byte>.Empty;

        ReadOnlySpan<byte> rlp = RlpSpan;
        Rlp.ValueDecoderContext ctx = rlp[offset..].AsRlpValueContext();
        int itemLength = ctx.PeekNextRlpLength();
        return rlp.Slice(offset, itemLength);
    }

    [InlineArray(17)]
    public struct ChildOffsetBuffer
    {
        private short _element;
    }
}
