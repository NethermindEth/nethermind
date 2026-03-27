// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Shared object pool for <see cref="RefCountingTrieNode"/> instances.
/// 4 type-specific ConcurrentQueues: full branch (532 bytes, no parse), regular branch, extension, leaf.
/// Active node counts tracked per type at the pool level.
/// </summary>
public sealed class RefCountingTrieNodePool
{
    public const int BranchBufferSize = 532;
    public const int ExtensionBufferSize = 96;
    public const int LeafBufferSize = 160;
    private const int FullBranchRlpLength = 532;

    private static readonly RefCountingTrieNode.ChildOffsetBuffer s_fullBranchOffsets = CreateFullBranchOffsets();

    private static RefCountingTrieNode.ChildOffsetBuffer CreateFullBranchOffsets()
    {
        RefCountingTrieNode.ChildOffsetBuffer offsets = default;
        for (int i = 0; i < 16; i++) offsets[i] = (short)(3 + i * 33);
        return offsets;
    }

    private readonly ConcurrentQueue<RefCountingTrieNode> _fullBranchPool = new();
    private readonly ConcurrentQueue<RefCountingTrieNode> _branchPool = new();
    private readonly ConcurrentQueue<RefCountingTrieNode> _extensionPool = new();
    private readonly ConcurrentQueue<RefCountingTrieNode> _leafPool = new();

    private long _activeFullBranchCount;
    private long _activeBranchCount;
    private long _activeExtensionCount;
    private long _activeLeafCount;

    private long _rentedFullBranchCount;
    private long _rentedBranchCount;
    private long _rentedExtensionCount;
    private long _rentedLeafCount;

    public long ActiveFullBranchCount => Volatile.Read(ref _activeFullBranchCount);
    public long ActiveBranchCount => Volatile.Read(ref _activeBranchCount);
    public long ActiveExtensionCount => Volatile.Read(ref _activeExtensionCount);
    public long ActiveLeafCount => Volatile.Read(ref _activeLeafCount);
    public long ActiveCount => ActiveFullBranchCount + ActiveBranchCount + ActiveExtensionCount + ActiveLeafCount;

    public RefCountingTrieNodePool(int maxPooled = 4096) { }

    internal RefCountingTrieNode Rent(RefCountingRlpNodePoolTracker tracker, ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        if (rlp.Length == FullBranchRlpLength)
            return RentFullBranch(tracker, hash, rlp);

        // Single-pass: classify and parse metadata
        Span<short> offsets = stackalloc short[16];
        NodeType nodeType = ClassifyAndParse(rlp, offsets);

        RefCountingTrieNode node = RentFromPool(nodeType);
        node.SetTracker(tracker);

        byte[] buffer = node.Rlp.UnderlyingArray!;
        rlp.CopyTo(buffer);
        node.Rlp = new CappedArray<byte>(buffer, rlp.Length);
        node.Initialize(hash, nodeType, node.Rlp);
        for (int i = 0; i < 16; i++)
            node.ChildOffsets[i] = offsets[i];

        IncrementActiveCount(nodeType);
        IncrementRentCount(nodeType);
        return node;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RefCountingTrieNode RentFullBranch(RefCountingRlpNodePoolTracker tracker, ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        RefCountingTrieNode node;
        if (!_fullBranchPool.TryDequeue(out node!))
        {
            Interlocked.Increment(ref Nethermind.Trie.Pruning.Metrics.CreatedPooledNodeCount);
            node = new RefCountingTrieNode();
            node.Rlp = new CappedArray<byte>(new byte[BranchBufferSize], 0);
            node.ChildOffsets = s_fullBranchOffsets;
        }

        node.SetTracker(tracker);
        byte[] buffer = node.Rlp.UnderlyingArray!;
        rlp.CopyTo(buffer);
        node.Rlp = new CappedArray<byte>(buffer, rlp.Length);
        node.Initialize(hash, NodeType.Branch, node.Rlp);

        Interlocked.Increment(ref _activeFullBranchCount);
        Interlocked.Increment(ref _rentedFullBranchCount);
        return node;
    }

    internal void Return(RefCountingTrieNode node)
    {
        DecrementActiveCount(node);
        if (node.RlpLength == FullBranchRlpLength && node.NodeType == NodeType.Branch)
        {
            // Full branch: don't clear offsets — they're always the same
            _fullBranchPool.Enqueue(node);
        }
        else
        {
            node.ChildOffsets = default;
            ReturnToPool(node.NodeType, node);
        }
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

    public long RentedFullBranchCount => Volatile.Read(ref _rentedFullBranchCount);
    public long RentedBranchCount => Volatile.Read(ref _rentedBranchCount);
    public long RentedExtensionCount => Volatile.Read(ref _rentedExtensionCount);
    public long RentedLeafCount => Volatile.Read(ref _rentedLeafCount);

    private void IncrementRentCount(NodeType nodeType)
    {
        switch (nodeType)
        {
            case NodeType.Branch: Interlocked.Increment(ref _rentedBranchCount); break;
            case NodeType.Extension: Interlocked.Increment(ref _rentedExtensionCount); break;
            default: Interlocked.Increment(ref _rentedLeafCount); break;
        }
    }

    private void IncrementActiveCount(NodeType nodeType)
    {
        switch (nodeType)
        {
            case NodeType.Branch: Interlocked.Increment(ref _activeBranchCount); break;
            case NodeType.Extension: Interlocked.Increment(ref _activeExtensionCount); break;
            default: Interlocked.Increment(ref _activeLeafCount); break;
        }
    }

    private void DecrementActiveCount(RefCountingTrieNode node)
    {
        if (node.RlpLength == FullBranchRlpLength && node.NodeType == NodeType.Branch)
            Interlocked.Decrement(ref _activeFullBranchCount);
        else switch (node.NodeType)
        {
            case NodeType.Branch: Interlocked.Decrement(ref _activeBranchCount); break;
            case NodeType.Extension: Interlocked.Decrement(ref _activeExtensionCount); break;
            default: Interlocked.Decrement(ref _activeLeafCount); break;
        }
    }

    /// <summary>Parse item offsets using ulong empty-skip trick. Returns item count.</summary>
    private static int ParseOffsets(ReadOnlySpan<byte> rlp, Span<short> offsets, ref Rlp.ValueDecoderContext ctx, int endPosition)
    {
        int i = 0;
        while (i < 16 && ctx.Position < endPosition)
        {
            ulong val = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(rlp), ctx.Position));
            int emptyCount = Math.Min(BitOperations.TrailingZeroCount(val ^ 0x8080808080808080UL) / 8, 16 - i);
            if (emptyCount > 0)
            {
                ctx.Position += emptyCount;
                i += emptyCount;
                continue;
            }

            offsets[i] = (short)ctx.Position;
            ctx.SkipItem();
            i++;
        }
        // Skip value slot (item 17) for branches — don't care about it
        return i;
    }

    /// <summary>
    /// Single-pass: determine node type and parse child offsets.
    /// </summary>
    private static NodeType ClassifyAndParse(ReadOnlySpan<byte> rlp, Span<short> offsets)
    {
        if (rlp.IsEmpty) return NodeType.Leaf;

        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        int sequenceLength = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + sequenceLength;

        // Parse all items using the ulong trick. Count how many we got.
        int itemCount = ParseOffsets(rlp, offsets, ref ctx, endPosition);

        if (itemCount > 2) return NodeType.Branch;

        // 2-item node: check compact key prefix at offsets[0] to distinguish ext vs leaf
        short keyOffset = offsets[0];
        byte firstByte = rlp[keyOffset] < 0x80 ? rlp[keyOffset] : rlp[keyOffset + (rlp[keyOffset] < 0xB8 ? 1 : 2 + rlp[keyOffset] - 0xB7)];
        if ((firstByte & 0x20) != 0)
        {
            offsets[0] = 0; // leaf has no child offset
            return NodeType.Leaf;
        }

        // Extension: child offset is at offsets[1], move it to offsets[0]
        offsets[0] = offsets[1];
        offsets[1] = 0;
        return NodeType.Extension;
    }
}
