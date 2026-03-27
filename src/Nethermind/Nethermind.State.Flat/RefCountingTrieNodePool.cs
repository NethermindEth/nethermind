// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Shared object pool for <see cref="RefCountingTrieNode"/> instances.
/// 3 type-specific pools, each holding a RefCountingTrieNode with its byte[] buffer already attached.
/// Single dequeue per Rent, single enqueue per Return.
/// </summary>
public sealed class RefCountingTrieNodePool
{
    public const int BranchBufferSize = 544;
    public const int ExtensionBufferSize = 96;
    public const int LeafBufferSize = 160;

    private readonly ConcurrentQueue<RefCountingTrieNode> _branchPool = new();
    private readonly ConcurrentQueue<RefCountingTrieNode> _extensionPool = new();
    private readonly ConcurrentQueue<RefCountingTrieNode> _leafPool = new();

    public RefCountingTrieNodePool(int maxPooled = 4096) { }

    internal RefCountingTrieNode Rent(RefCountingRlpNodePoolTracker tracker, ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        NodeType nodeType = DetermineNodeType(rlp);
        RefCountingTrieNode node = RentFromPool(nodeType);
        node.SetTracker(tracker);

        byte[] buffer = node.Rlp.UnderlyingArray!;
        rlp.CopyTo(buffer);
        node.Rlp = new CappedArray<byte>(buffer, rlp.Length);

        node.Initialize(hash, nodeType, node.Rlp);
        ParseMetadata(node, rlp);
        return node;
    }

    internal void Return(RefCountingTrieNode node)
    {
        // Keep the buffer attached — it will be reused on next rent from the same pool
        node.ChildOffsets = default;
        ReturnToPool(node.NodeType, node);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RefCountingTrieNode RentFromPool(NodeType nodeType)
    {
        ConcurrentQueue<RefCountingTrieNode> pool = GetPool(nodeType);
        if (pool.TryDequeue(out RefCountingTrieNode? node))
            return node;

        Interlocked.Increment(ref Nethermind.Trie.Pruning.Metrics.CreatedPooledNodeCount);
        RefCountingTrieNode fresh = new();
        int bufferSize = nodeType switch
        {
            NodeType.Branch => BranchBufferSize,
            NodeType.Extension => ExtensionBufferSize,
            _ => LeafBufferSize,
        };
        fresh.Rlp = new CappedArray<byte>(new byte[bufferSize], 0);
        return fresh;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnToPool(NodeType nodeType, RefCountingTrieNode node) =>
        GetPool(nodeType).Enqueue(node);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ConcurrentQueue<RefCountingTrieNode> GetPool(NodeType nodeType) => nodeType switch
    {
        NodeType.Branch => _branchPool,
        NodeType.Extension => _extensionPool,
        _ => _leafPool,
    };

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
}
