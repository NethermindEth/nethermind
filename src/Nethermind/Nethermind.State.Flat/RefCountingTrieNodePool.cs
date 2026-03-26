// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Shared object pool for <see cref="RefCountingTrieNode"/> instances and their type-specific byte[] RLP buffers.
/// </summary>
public sealed class RefCountingTrieNodePool
{
    public const int BranchBufferSize = 544;
    public const int ExtensionBufferSize = 96;
    public const int LeafBufferSize = 160;

    private readonly ObjectPool<RefCountingTrieNode> _shellPool;
    private readonly ObjectPool<byte[]> _branchBufferPool;
    private readonly ObjectPool<byte[]> _extensionBufferPool;
    private readonly ObjectPool<byte[]> _leafBufferPool;

    public RefCountingTrieNodePool(int maxPooled = 4096)
    {
        _shellPool = new DefaultObjectPool<RefCountingTrieNode>(new ShellPolicy(), maxPooled);
        _branchBufferPool = new DefaultObjectPool<byte[]>(new ByteArrayPolicy(BranchBufferSize), maxPooled);
        _extensionBufferPool = new DefaultObjectPool<byte[]>(new ByteArrayPolicy(ExtensionBufferSize), maxPooled);
        _leafBufferPool = new DefaultObjectPool<byte[]>(new ByteArrayPolicy(LeafBufferSize), maxPooled);
    }

    internal RefCountingTrieNode Rent(RefCountingRlpNodePoolTracker tracker, ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        RefCountingTrieNode shell = _shellPool.Get();
        shell.SetTracker(tracker);

        NodeType nodeType = DetermineNodeType(rlp);
        byte[] buffer = RentBuffer(nodeType);
        rlp.CopyTo(buffer);
        CappedArray<byte> cappedRlp = new(buffer, rlp.Length);

        shell.Initialize(hash, nodeType, cappedRlp);
        ParseMetadata(shell, rlp);
        return shell;
    }

    internal void Return(RefCountingTrieNode node)
    {
        byte[]? buffer = node.Rlp.UnderlyingArray;
        if (buffer is not null)
        {
            ReturnBuffer(node.NodeType, buffer);
        }
        node.Rlp = default;
        _shellPool.Return(node);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] RentBuffer(NodeType nodeType) => nodeType switch
    {
        NodeType.Branch => _branchBufferPool.Get(),
        NodeType.Extension => _extensionBufferPool.Get(),
        _ => _leafBufferPool.Get(),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnBuffer(NodeType nodeType, byte[] buffer)
    {
        switch (nodeType)
        {
            case NodeType.Branch: _branchBufferPool.Return(buffer); break;
            case NodeType.Extension: _extensionBufferPool.Return(buffer); break;
            default: _leafBufferPool.Return(buffer); break;
        }
    }

    private static void ParseMetadata(RefCountingTrieNode node, ReadOnlySpan<byte> rlp)
    {
        if (node.NodeType == NodeType.Branch)
        {
            Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
            int sequenceLength = ctx.ReadSequenceLength();
            int endPosition = ctx.Position + sequenceLength;

            for (int i = 0; i < 17 && ctx.Position < endPosition; i++)
            {
                byte prefix = rlp[ctx.Position];
                if (prefix == 0x80)
                {
                    node.ChildOffsets[i] = 0;
                    ctx.Position++;
                }
                else
                {
                    node.ChildOffsets[i] = (short)ctx.Position;
                    ctx.SkipItem();
                }
            }
        }
        else if (node.NodeType == NodeType.Extension)
        {
            Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
            ctx.ReadSequenceLength();

            if (rlp[ctx.Position] < 0x80)
            {
                ctx.Position++;
            }
            else
            {
                (int prefixLen, int contentLen) = ctx.ReadPrefixAndContentLength();
                ctx.Position += contentLen;
            }

            node.ChildOffsets[0] = (short)ctx.Position;
        }
    }

    private static NodeType DetermineNodeType(ReadOnlySpan<byte> rlp)
    {
        if (rlp.IsEmpty) return NodeType.Leaf;

        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        int sequenceLength = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + sequenceLength;
        int itemCount = ctx.PeekNumberOfItemsRemaining(endPosition, maxSearch: 3);

        if (itemCount != 2) return NodeType.Branch;

        byte firstByte;
        if (rlp[ctx.Position] < 0x80)
        {
            firstByte = rlp[ctx.Position];
        }
        else
        {
            (int prefixLen, int contentLen) = ctx.ReadPrefixAndContentLength();
            firstByte = rlp[ctx.Position];
        }

        return (firstByte & 0x20) != 0 ? NodeType.Leaf : NodeType.Extension;
    }

    private sealed class ShellPolicy : PooledObjectPolicy<RefCountingTrieNode>
    {
        public override RefCountingTrieNode Create() => new();
        public override bool Return(RefCountingTrieNode obj) => true;
    }

    private sealed class ByteArrayPolicy(int size) : PooledObjectPolicy<byte[]>
    {
        public override byte[] Create() => new byte[size];
        public override bool Return(byte[] obj) => obj.Length == size;
    }
}
