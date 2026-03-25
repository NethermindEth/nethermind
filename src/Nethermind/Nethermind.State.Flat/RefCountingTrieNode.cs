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
/// Created exclusively by <see cref="RefCountingTrieNodePool"/> and returned to it on final dispose.
/// </summary>
public sealed class RefCountingTrieNode : RefCountingDisposable
{
    private readonly RefCountingTrieNodePool _pool;

    public ValueHash256 Hash;
    public TrieNodeMetadata Metadata;
    public TrieNodeRlp Rlp;

    internal RefCountingTrieNode(RefCountingTrieNodePool pool) : base(initialCount: 0) =>
        _pool = pool;

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
        Rlp.Length = 0;
        Metadata = default;
        Hash = default;
        _pool.Return(this);
    }

    /// <summary>Tries to acquire a lease. Returns false if already disposed.</summary>
    public new bool TryAcquireLease() => base.TryAcquireLease();

    /// <summary>Current lease count. For diagnostics only.</summary>
    public long LeaseCount => Volatile.Read(ref _leases.Value);

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
