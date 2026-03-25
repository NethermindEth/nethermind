// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Delegate for loading a trie node identified by its path and hash.
/// Returns a leased <see cref="RefCountingTrieNode"/> (caller must dispose) or <c>null</c> on miss.
/// </summary>
internal delegate RefCountingTrieNode? NodeLoader(TreePath path, Hash256 hash);

/// <summary>
/// Traverses a Merkle Patricia Trie using <see cref="RefCountingTrieNode"/> with pre-parsed metadata.
/// Branch traversal uses <see cref="TrieNodeMetadata.ChildOffsets"/> to jump directly to child positions
/// without re-parsing the RLP sequence. For inline nodes (no hash), falls back to local RLP parsing.
/// </summary>
internal static class RlpTrieTraversal
{
    private const int MaxKeyStackAlloc = 64; // 32 bytes * 2 nibbles = 64 nibbles

    /// <summary>
    /// Traverses the trie along the path described by <paramref name="rawKey"/>, loading and caching
    /// each node via <paramref name="nodeLoader"/>. No <see cref="TrieNode"/> objects are created.
    /// </summary>
    public static void WarmUpPath(
        NodeLoader nodeLoader,
        Hash256 rootHash,
        ReadOnlySpan<byte> rawKey) =>
        TryRead(nodeLoader, rootHash, rawKey, out _, readValue: false);

    /// <summary>
    /// Traverses the trie along the path described by <paramref name="rawKey"/> and returns the
    /// leaf value bytes if the key exists.
    /// </summary>
    public static bool TryRead(
        NodeLoader nodeLoader,
        Hash256 rootHash,
        ReadOnlySpan<byte> rawKey,
        out byte[]? value,
        bool readValue = true)
    {
        int nibblesCount = 2 * rawKey.Length;
        byte[]? rentedArray = null;
        Span<byte> nibbles = nibblesCount <= MaxKeyStackAlloc
            ? stackalloc byte[MaxKeyStackAlloc]
            : (rentedArray = ArrayPool<byte>.Shared.Rent(nibblesCount));

        try
        {
            nibbles = nibbles[..nibblesCount];
            Nibbles.BytesToNibbleBytes(rawKey, nibbles);

            TreePath path = TreePath.Empty;
            return TryReadHashedNode(nodeLoader, ref path, rootHash, nibbles, readValue, out value);
        }
        finally
        {
            if (rentedArray is not null) ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    private static bool TryReadHashedNode(
        NodeLoader nodeLoader,
        ref TreePath path,
        Hash256 hash,
        Span<byte> remainingNibbles,
        bool readValue,
        out byte[]? value)
    {
        value = null;

        if (hash == Keccak.EmptyTreeHash) return false;

        // Iterative loop for hash-referenced nodes; recurse only for inline nodes.
        while (true)
        {
            RefCountingTrieNode? node = nodeLoader(path, hash);
            if (node is null) return false;

            try
            {
                bool continueLoop = TraverseNode(nodeLoader, ref path, node, remainingNibbles, readValue, out Hash256? nextHash, out int nibblesConsumed, out byte[]? leafValue);
                if (!continueLoop)
                {
                    value = leafValue;
                    return leafValue is not null;
                }

                if (nextHash is null) return false;

                remainingNibbles = remainingNibbles[nibblesConsumed..];
                hash = nextHash;
            }
            finally
            {
                node.Dispose();
            }
        }
    }

    /// <summary>
    /// Dispatches traversal based on the node's pre-parsed <see cref="TrieNodeMetadata.NodeType"/>.
    /// Returns false when traversal should stop (leaf reached, missing child, key mismatch).
    /// </summary>
    private static bool TraverseNode(
        NodeLoader nodeLoader,
        ref TreePath path,
        RefCountingTrieNode node,
        Span<byte> remainingNibbles,
        bool readValue,
        out Hash256? nextHash,
        out int nibblesConsumed,
        out byte[]? leafValue)
    {
        nextHash = null;
        nibblesConsumed = 0;
        leafValue = null;

        return node.Metadata.NodeType switch
        {
            NodeType.Branch => TraverseBranch(nodeLoader, ref path, node, remainingNibbles, out nextHash, out nibblesConsumed, out leafValue),
            NodeType.Extension => TraverseExtensionOrLeaf(nodeLoader, ref path, node, remainingNibbles, readValue, out nextHash, out nibblesConsumed, out leafValue),
            NodeType.Leaf => TraverseLeaf(node, remainingNibbles, readValue, out leafValue),
            _ => false
        };
    }

    private static bool TraverseBranch(
        NodeLoader nodeLoader,
        ref TreePath path,
        RefCountingTrieNode node,
        Span<byte> remainingNibbles,
        out Hash256? nextHash,
        out int nibblesConsumed,
        out byte[]? leafValue)
    {
        nextHash = null;
        nibblesConsumed = 0;
        leafValue = null;

        if (remainingNibbles.IsEmpty) return false;

        int nib = remainingNibbles[0];
        short childOffset = node.Metadata.ChildOffsets[nib];
        if (childOffset == 0) return false; // empty child

        path.AppendMut(nib);
        nibblesConsumed = 1;

        ReadOnlySpan<byte> rlp = node.Rlp.AsSpan();
        return TryReadChildRef(nodeLoader, ref path, rlp, childOffset, remainingNibbles[1..], true, out nextHash, out int childNibblesConsumed, out leafValue)
            && (nibblesConsumed += childNibblesConsumed) >= 0; // always true, just adds the consumed count
    }

    private static bool TraverseExtensionOrLeaf(
        NodeLoader nodeLoader,
        ref TreePath path,
        RefCountingTrieNode node,
        Span<byte> remainingNibbles,
        bool readValue,
        out Hash256? nextHash,
        out int nibblesConsumed,
        out byte[]? leafValue)
    {
        nextHash = null;
        nibblesConsumed = 0;
        leafValue = null;

        ReadOnlySpan<byte> rlp = node.Rlp.AsSpan();
        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        ctx.ReadSequenceLength();

        // First item: compact-encoded path prefix
        ReadOnlySpan<byte> compactKeyBytes;
        if (rlp[ctx.Position] < 0x80)
        {
            compactKeyBytes = rlp.Slice(ctx.Position, 1);
            ctx.Position++;
        }
        else
        {
            (int prefixLen, int contentLen) = ctx.ReadPrefixAndContentLength();
            if (contentLen == 0) return false;
            compactKeyBytes = ctx.Read(contentLen);
        }

        byte firstByte = compactKeyBytes[0];
        bool isLeaf = (firstByte & 0x20) != 0;
        bool isOdd = (firstByte & 0x10) != 0;
        int keyNibbleCount = (compactKeyBytes.Length - 1) * 2 + (isOdd ? 1 : 0);

        if (isLeaf)
        {
            if (!readValue) return false;
            if (remainingNibbles.Length != keyNibbleCount) return false;
            if (!MatchNibbles(compactKeyBytes, firstByte, isOdd, keyNibbleCount, remainingNibbles)) return false;

            leafValue = ctx.DecodeByteArray();
            return false; // stop traversal; leaf found
        }

        // Extension node
        if (remainingNibbles.Length < keyNibbleCount) return false;
        if (!MatchNibbles(compactKeyBytes, firstByte, isOdd, keyNibbleCount, remainingNibbles)) return false;

        path.AppendMut(remainingNibbles[..keyNibbleCount]);
        nibblesConsumed = keyNibbleCount;

        // Second item: child reference (at the pre-parsed offset)
        short childOffset = node.Metadata.ChildOffsets[0];
        if (childOffset == 0) return false;

        bool result = TryReadChildRef(nodeLoader, ref path, rlp, childOffset, remainingNibbles[keyNibbleCount..], readValue, out nextHash, out int childNibblesConsumed, out leafValue);
        nibblesConsumed += childNibblesConsumed;
        return result;
    }

    private static bool TraverseLeaf(
        RefCountingTrieNode node,
        Span<byte> remainingNibbles,
        bool readValue,
        out byte[]? leafValue)
    {
        leafValue = null;
        if (!readValue) return false;

        ReadOnlySpan<byte> rlp = node.Rlp.AsSpan();
        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        ctx.ReadSequenceLength();

        // First item: compact key
        ReadOnlySpan<byte> compactKeyBytes;
        if (rlp[ctx.Position] < 0x80)
        {
            compactKeyBytes = rlp.Slice(ctx.Position, 1);
            ctx.Position++;
        }
        else
        {
            (int prefixLen, int contentLen) = ctx.ReadPrefixAndContentLength();
            if (contentLen == 0) return false;
            compactKeyBytes = ctx.Read(contentLen);
        }

        byte firstByte = compactKeyBytes[0];
        bool isOdd = (firstByte & 0x10) != 0;
        int keyNibbleCount = (compactKeyBytes.Length - 1) * 2 + (isOdd ? 1 : 0);

        if (remainingNibbles.Length != keyNibbleCount) return false;
        if (!MatchNibbles(compactKeyBytes, firstByte, isOdd, keyNibbleCount, remainingNibbles)) return false;

        leafValue = ctx.DecodeByteArray();
        return false; // stop traversal
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchNibbles(
        ReadOnlySpan<byte> compactKeyBytes,
        byte firstByte,
        bool isOdd,
        int keyNibbleCount,
        Span<byte> remainingNibbles)
    {
        int cIdx = 0;
        int nIdx = 0;

        if (isOdd)
        {
            if (remainingNibbles[nIdx++] != (firstByte & 0x0F)) return false;
            cIdx = 1;
        }
        else
        {
            cIdx = 1;
        }

        while (cIdx < compactKeyBytes.Length)
        {
            byte b = compactKeyBytes[cIdx++];
            if (remainingNibbles[nIdx++] != (b >> 4)) return false;
            if (nIdx < keyNibbleCount && remainingNibbles[nIdx++] != (b & 0x0F)) return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a child reference at <paramref name="offset"/> in <paramref name="rlp"/>.
    /// Handles empty refs (0x80), hash refs (0xA0 + 32 bytes), and inline nodes (sequence).
    /// </summary>
    private static bool TryReadChildRef(
        NodeLoader nodeLoader,
        ref TreePath path,
        ReadOnlySpan<byte> rlp,
        int offset,
        Span<byte> remainingNibbles,
        bool readValue,
        out Hash256? nextHash,
        out int nibblesConsumed,
        out byte[]? leafValue)
    {
        nextHash = null;
        nibblesConsumed = 0;
        leafValue = null;

        if (offset >= rlp.Length) return false;

        byte prefix = rlp[offset];

        if (prefix == 0x80)
        {
            return false; // empty child
        }

        if (prefix == 0xA0)
        {
            // Hash reference (0xA0 = 0x80 + 32)
            if (offset + 33 > rlp.Length) return false;
            nextHash = new Hash256(rlp.Slice(offset + 1, 32));
            return true;
        }

        if (prefix >= 0xC0)
        {
            // Inline node — parse directly from parent's RLP (no hash, so never cached independently)
            Rlp.ValueDecoderContext ctx = rlp[offset..].AsRlpValueContext();
            int inlineLen = ctx.PeekNextRlpLength();
            ReadOnlySpan<byte> inlineRlp = rlp.Slice(offset, inlineLen);
            TraverseInlineNode(nodeLoader, ref path, inlineRlp, remainingNibbles, readValue, out nextHash, out nibblesConsumed, out leafValue);
            return nextHash is not null;
        }

        return false;
    }

    /// <summary>
    /// Parses an inline node from raw RLP bytes. Inline nodes have no hash and are not cached,
    /// so we parse them locally without going through the NodeLoader.
    /// </summary>
    private static void TraverseInlineNode(
        NodeLoader nodeLoader,
        ref TreePath path,
        ReadOnlySpan<byte> rlp,
        Span<byte> remainingNibbles,
        bool readValue,
        out Hash256? nextHash,
        out int nibblesConsumed,
        out byte[]? leafValue)
    {
        nextHash = null;
        nibblesConsumed = 0;
        leafValue = null;

        Rlp.ValueDecoderContext ctx = rlp.AsRlpValueContext();
        int sequenceLength = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + sequenceLength;
        int itemCount = ctx.PeekNumberOfItemsRemaining(endPosition, maxSearch: 3);

        if (itemCount == 2)
        {
            // Extension or leaf inline node — parse compact key
            ReadOnlySpan<byte> compactKeyBytes;
            if (rlp[ctx.Position] < 0x80)
            {
                compactKeyBytes = rlp.Slice(ctx.Position, 1);
                ctx.Position++;
            }
            else
            {
                (int prefixLen, int contentLen) = ctx.ReadPrefixAndContentLength();
                if (contentLen == 0) return;
                compactKeyBytes = ctx.Read(contentLen);
            }

            byte firstByte = compactKeyBytes[0];
            bool isLeaf = (firstByte & 0x20) != 0;
            bool isOdd = (firstByte & 0x10) != 0;
            int keyNibbleCount = (compactKeyBytes.Length - 1) * 2 + (isOdd ? 1 : 0);

            if (isLeaf)
            {
                if (!readValue) return;
                if (remainingNibbles.Length != keyNibbleCount) return;
                if (!MatchNibbles(compactKeyBytes, firstByte, isOdd, keyNibbleCount, remainingNibbles)) return;
                leafValue = ctx.DecodeByteArray();
                return;
            }

            // Inline extension
            if (remainingNibbles.Length < keyNibbleCount) return;
            if (!MatchNibbles(compactKeyBytes, firstByte, isOdd, keyNibbleCount, remainingNibbles)) return;

            path.AppendMut(remainingNibbles[..keyNibbleCount]);
            nibblesConsumed = keyNibbleCount;

            // Child of inline extension
            int childOffset = ctx.Position;
            bool result = TryReadChildRef(nodeLoader, ref path, rlp, childOffset, remainingNibbles[keyNibbleCount..], readValue, out nextHash, out int childNibblesConsumed, out leafValue);
            nibblesConsumed += childNibblesConsumed;
        }
        else
        {
            // Inline branch — skip to correct child
            if (remainingNibbles.IsEmpty) return;
            int nib = remainingNibbles[0];
            for (int i = 0; i < nib; i++) ctx.SkipItem();

            path.AppendMut(nib);
            nibblesConsumed = 1;

            int childOffset = ctx.Position;
            TryReadChildRef(nodeLoader, ref path, rlp, childOffset, remainingNibbles[1..], readValue, out nextHash, out int childNibblesConsumed, out leafValue);
            nibblesConsumed += childNibblesConsumed;
        }
    }
}
