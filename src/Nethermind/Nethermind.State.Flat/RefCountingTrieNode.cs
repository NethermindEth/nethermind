// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A poolable, ref-counted trie node that stores RLP inline with pre-parsed metadata.
/// Created by <see cref="RefCountingTrieNodePool"/> and tracked by <see cref="RefCountingRlpNodePoolTracker"/>.
/// </summary>
public sealed class RefCountingTrieNode : RefCountingDisposable
{
    private RefCountingRlpNodePoolTracker _tracker = null!;

    public ValueHash256 Hash;
    public TrieNodeMetadata Metadata;
    public TrieNodeRlp Rlp;

    /// <summary>
    /// Direct references to child nodes (branch children 0-15 only).
    /// Only valid for branch nodes whose path nibble length is divisible by 2.
    /// </summary>
    public ChildRefBuffer Children;

    internal RefCountingTrieNode() : base(initialCount: 0) { }

    /// <summary>Binds this node to a tracker. Called by the pool before each use.</summary>
    internal void SetTracker(RefCountingRlpNodePoolTracker tracker) =>
        _tracker = tracker;

    /// <summary>
    /// Initializes this node with the given hash and RLP data. Eagerly parses metadata.
    /// After this call, the node has exactly one lease.
    /// </summary>
    internal void Initialize(ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        Hash = hash;
        Rlp.Set(rlp);
        ParseMetadata();
        _leases.Value = 1;
    }

    protected override void CleanUp()
    {
        for (int i = 0; i < 16; i++)
        {
            RefCountingTrieNode? child = Interlocked.Exchange(ref Children[i], null);
            child?.Dispose();
        }

        Rlp.Length = 0;
        Metadata = default;
        Hash = default;
        _tracker.Return(this);
    }

    /// <summary>Tries to acquire a lease. Returns false if already disposed.</summary>
    public new bool TryAcquireLease() => base.TryAcquireLease();

    /// <summary>Current lease count. For diagnostics only.</summary>
    public long LeaseCount => Volatile.Read(ref _leases.Value);

    /// <summary>
    /// Returns a leased direct child reference at <paramref name="index"/> if it matches <paramref name="expectedHash"/>.
    /// Caller must dispose the returned node. Returns <c>null</c> on miss, hash mismatch, or if the child was already disposed.
    /// Only valid for branch nodes whose path nibble length is divisible by 2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RefCountingTrieNode? TryGetChildRef(int index, in ValueHash256 expectedHash)
    {
        RefCountingTrieNode? child = Volatile.Read(ref Children[index]);
        if (child is null) return null;
        if (!child.TryAcquireLease()) return null;
        if (child.Hash == expectedHash) return child;
        child.Dispose();
        return null;
    }

    /// <summary>
    /// Atomically sets a direct child reference at <paramref name="index"/>. Acquires a lease on the child
    /// for parent ownership. If the slot is already occupied, the lease is released (first-writer-wins).
    /// Only valid for branch nodes whose path nibble length is divisible by 2.
    /// </summary>
    public void SetChildRef(int index, RefCountingTrieNode child)
    {
        child.AcquireLease(); // +1 for parent ownership
        if (Interlocked.CompareExchange(ref Children[index], child, null) is not null)
        {
            child.Dispose(); // slot already occupied, release the lease we just took
        }
    }

    /// <summary>
    /// Returns the 32-byte hash of child at <paramref name="index"/> by reading from the RLP at the stored offset.
    /// Returns <c>null</c> if the child offset is 0 (empty/absent).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Hash256? GetChildHash(int index)
    {
        short offset = Metadata.ChildOffsets[index];
        if (offset == 0) return null;

        ReadOnlySpan<byte> rlp = Rlp.AsSpan();
        // Child hash ref in RLP: 0xA0 prefix byte followed by 32 bytes of hash
        if (offset < rlp.Length && rlp[offset] == 0xA0 && offset + 33 <= rlp.Length)
        {
            return new Hash256(rlp.Slice(offset + 1, 32));
        }

        return null;
    }

    /// <summary>
    /// Returns the raw RLP bytes of the child item at <paramref name="index"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetChildRlp(int index)
    {
        short offset = Metadata.ChildOffsets[index];
        if (offset == 0) return ReadOnlySpan<byte>.Empty;

        ReadOnlySpan<byte> rlp = Rlp.AsSpan();
        Rlp.ValueDecoderContext ctx = rlp[offset..].AsRlpValueContext();
        int itemLength = ctx.PeekNextRlpLength();
        return rlp.Slice(offset, itemLength);
    }

    private void ParseMetadata()
    {
        Metadata = default;
        ReadOnlySpan<byte> rlp = Rlp.AsSpan();
        if (rlp.IsEmpty) return;

        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        int sequenceLength = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + sequenceLength;

        int itemCount = ctx.PeekNumberOfItemsRemaining(endPosition, maxSearch: 3);

        if (itemCount == 2)
        {
            // Extension or Leaf — determined by the compact encoding prefix
            int keyStart = ctx.Position;
            byte firstByte;
            if (rlp[keyStart] < 0x80)
            {
                firstByte = rlp[keyStart];
                ctx.Position++;
            }
            else
            {
                (int prefixLen, int contentLen) = ctx.ReadPrefixAndContentLength();
                firstByte = rlp[ctx.Position];
                ctx.Position += contentLen;
            }

            bool isLeaf = (firstByte & 0x20) != 0;
            Metadata.NodeType = isLeaf ? NodeType.Leaf : NodeType.Extension;

            // Store offset of the second item (child for extension, value for leaf)
            Metadata.ChildOffsets[0] = (short)ctx.Position;
        }
        else
        {
            // Branch node (17 items)
            Metadata.NodeType = NodeType.Branch;

            for (int i = 0; i < 17 && ctx.Position < endPosition; i++)
            {
                byte prefix = rlp[ctx.Position];
                if (prefix == 0x80)
                {
                    // Empty child
                    Metadata.ChildOffsets[i] = 0;
                    ctx.Position++;
                }
                else
                {
                    Metadata.ChildOffsets[i] = (short)ctx.Position;
                    ctx.SkipItem();
                }
            }
        }
    }
}

/// <summary>
/// Pre-parsed metadata for a trie node. Stores the node type and byte offsets of each child's
/// RLP item within the parent <see cref="TrieNodeRlp"/> buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TrieNodeMetadata
{
    public NodeType NodeType;

    /// <summary>
    /// Byte offsets into the <see cref="TrieNodeRlp"/> buffer where each child's RLP item starts.
    /// 17 slots: indices 0-15 for branch children, index 16 for the value slot (or index 0 for extension child).
    /// A value of 0 means the child is empty/absent.
    /// </summary>
    public ChildOffsetBuffer ChildOffsets;

    [InlineArray(17)]
    public struct ChildOffsetBuffer
    {
        private short _element;
    }
}

/// <summary>
/// Inline array of 16 direct child references for branch nodes.
/// </summary>
[InlineArray(16)]
public struct ChildRefBuffer
{
    private RefCountingTrieNode? _element;
}
