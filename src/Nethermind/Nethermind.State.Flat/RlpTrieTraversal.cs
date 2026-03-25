// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Delegate for loading RLP bytes for a trie node identified by its path and hash.
/// Writes into <paramref name="target"/> and returns <c>true</c> on success.
/// </summary>
/// <remarks>
/// Cannot use <see cref="System.Func{T1,T2,T3,TResult}"/> because it does not support <c>ref</c> parameters.
/// </remarks>
internal delegate bool RlpLoader(TreePath path, Hash256 hash, ref TrieNodeRlp target);

/// <summary>
/// Traverses a Merkle Patricia Trie at the RLP level without creating <see cref="TrieNode"/> objects.
/// This eliminates GC pressure during warmup by working directly with raw RLP bytes.
/// </summary>
internal static class RlpTrieTraversal
{
    private const int MaxKeyStackAlloc = 64; // 32 bytes * 2 nibbles = 64 nibbles

    /// <summary>
    /// Traverses the trie along the path described by <paramref name="rawKey"/>, loading and caching
    /// each node's RLP via <paramref name="rlpLoader"/>. No <see cref="TrieNode"/> objects are created.
    /// </summary>
    /// <param name="rlpLoader">
    /// Loads RLP bytes for a node identified by its path and hash.
    /// Returns <c>false</c> if the node is not available (cache miss + no disk fallback).
    /// </param>
    /// <param name="rootHash">The root hash of the trie to traverse.</param>
    /// <param name="rawKey">The raw key bytes to look up (not nibble-encoded).</param>
    public static void WarmUpPath(
        RlpLoader rlpLoader,
        Hash256 rootHash,
        ReadOnlySpan<byte> rawKey)
    {
        TryRead(rlpLoader, rootHash, rawKey, out _);
    }

    /// <summary>
    /// Traverses the trie along the path described by <paramref name="rawKey"/> and returns the
    /// leaf value bytes if the key exists.
    /// </summary>
    /// <param name="rlpLoader">
    /// Loads RLP bytes for a node identified by its path and hash.
    /// Returns <c>false</c> if the node is not available.
    /// </param>
    /// <param name="rootHash">The root hash of the trie to traverse.</param>
    /// <param name="rawKey">The raw key bytes to look up (not nibble-encoded).</param>
    /// <param name="value">
    /// The raw leaf value bytes on success (same bytes returned by <c>PatriciaTree.Get</c>);
    /// <c>null</c> if the key is not found or the trie is unreachable.
    /// </param>
    /// <returns><c>true</c> if the key was found and <paramref name="value"/> is set.</returns>
    public static bool TryRead(
        RlpLoader rlpLoader,
        Hash256 rootHash,
        ReadOnlySpan<byte> rawKey,
        out byte[]? value)
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
            return TryReadHashedNode(rlpLoader, ref path, rootHash, nibbles, out value);
        }
        finally
        {
            if (rentedArray is not null) ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    private static bool TryReadHashedNode(
        RlpLoader rlpLoader,
        ref TreePath path,
        Hash256 hash,
        Span<byte> remainingNibbles,
        out byte[]? value)
    {
        value = null;

        // The empty tree root is a special sentinel that represents an empty trie — not a real node.
        if (hash == Keccak.EmptyTreeHash) return false;

        // Single stack-allocated buffer reused across all hash-node iterations in this path.
        // ~546 bytes on the stack, acceptable for warmer threads.
        TrieNodeRlp nodeBuffer = default;

        // Iterative loop for hash-referenced nodes; recurse only for inline nodes.
        while (true)
        {
            nodeBuffer.Length = 0;
            if (!rlpLoader(path, hash, ref nodeBuffer) || nodeBuffer.Length == 0) return false;

            bool continueLoop = TraverseNode(rlpLoader, ref path, nodeBuffer.AsSpan(), remainingNibbles, out Hash256? nextHash, out int nibblesConsumed, out byte[]? leafValue);
            if (!continueLoop)
            {
                value = leafValue;
                return leafValue is not null;
            }

            if (nextHash is null) return false;

            remainingNibbles = remainingNibbles[nibblesConsumed..];
            hash = nextHash;
        }
    }

    /// <summary>
    /// Parses one node from <paramref name="rlp"/> and determines the next hash and nibbles consumed.
    /// Returns false when traversal should stop (leaf reached, missing child, key mismatch).
    /// When a leaf is found and the key matches, <paramref name="leafValue"/> is set to the decoded value.
    /// </summary>
    private static bool TraverseNode(
        RlpLoader rlpLoader,
        ref TreePath path,
        ReadOnlySpan<byte> rlp,
        Span<byte> remainingNibbles,
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
            return TraverseExtensionOrLeaf(rlpLoader, ref path, rlp, ref ctx, remainingNibbles, out nextHash, out nibblesConsumed, out leafValue);
        }

        // Branch node (17 items) — branch values are always empty in the Ethereum state trie
        return TraverseBranch(rlpLoader, ref path, rlp, ref ctx, remainingNibbles, out nextHash, out nibblesConsumed, out leafValue);
    }

    private static bool TraverseExtensionOrLeaf(
        RlpLoader rlpLoader,
        ref TreePath path,
        ReadOnlySpan<byte> parentRlp,
        ref Rlp.ValueDecoderContext ctx,
        Span<byte> remainingNibbles,
        out Hash256? nextHash,
        out int nibblesConsumed,
        out byte[]? leafValue)
    {
        nextHash = null;
        nibblesConsumed = 0;
        leafValue = null;

        // First item: compact-encoded path prefix.
        // For single-byte values (< 0x80), ReadPrefixAndContentLength advances past
        // the byte, so Read(contentLen) would read wrong data. Handle this case directly.
        ReadOnlySpan<byte> compactKeyBytes;
        if (parentRlp[ctx.Position] < 0x80)
        {
            compactKeyBytes = parentRlp.Slice(ctx.Position, 1);
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
            // Leaf: remaining nibbles must exactly match the leaf's compact key
            if (remainingNibbles.Length != keyNibbleCount) return false;

            // Check nibble match (inline to avoid allocation)
            int compactIdx = 0;
            int nibIdx = 0;
            if (isOdd)
            {
                if (remainingNibbles[nibIdx++] != (firstByte & 0x0F)) return false;
                compactIdx = 1;
            }
            else
            {
                compactIdx = 1; // skip the prefix byte
            }

            while (compactIdx < compactKeyBytes.Length)
            {
                byte b = compactKeyBytes[compactIdx++];
                if (remainingNibbles[nibIdx++] != (b >> 4)) return false;
                if (nibIdx < keyNibbleCount && remainingNibbles[nibIdx++] != (b & 0x0F)) return false;
            }

            // Read leaf value (second RLP item) — matches TrieNode.Decoder behaviour
            leafValue = ctx.DecodeByteArray();
            return false; // stop traversal; leaf found
        }

        // Extension node: verify the remaining key starts with the extension's path
        if (remainingNibbles.Length < keyNibbleCount) return false;

        // Check nibble match (inline to avoid allocation)
        int cIdx = 0;
        int nIdx = 0;
        if (isOdd)
        {
            if (remainingNibbles[nIdx++] != (firstByte & 0x0F)) return false;
            cIdx = 1;
        }
        else
        {
            cIdx = 1; // skip the prefix byte
        }

        while (cIdx < compactKeyBytes.Length)
        {
            byte b = compactKeyBytes[cIdx++];
            if (remainingNibbles[nIdx++] != (b >> 4)) return false;
            if (nIdx < keyNibbleCount && remainingNibbles[nIdx++] != (b & 0x0F)) return false;
        }

        // Advance path by the extension key nibbles
        path.AppendMut(remainingNibbles[..keyNibbleCount]);
        nibblesConsumed = keyNibbleCount;

        // Second item: child reference
        bool result = TryReadChildRef(rlpLoader, ref path, parentRlp, ref ctx, remainingNibbles[keyNibbleCount..], out nextHash, out int childNibblesConsumed, out leafValue);
        nibblesConsumed += childNibblesConsumed;
        return result;
    }

    private static bool TraverseBranch(
        RlpLoader rlpLoader,
        ref TreePath path,
        ReadOnlySpan<byte> parentRlp,
        ref Rlp.ValueDecoderContext ctx,
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

        // Skip to the correct child slot
        for (int i = 0; i < nib; i++) ctx.SkipItem();

        path.AppendMut(nib);
        nibblesConsumed = 1;

        bool result = TryReadChildRef(rlpLoader, ref path, parentRlp, ref ctx, remainingNibbles[1..], out nextHash, out int childNibblesConsumed, out leafValue);
        nibblesConsumed += childNibblesConsumed;
        return result;
    }

    /// <summary>
    /// Reads a child reference at the current position in <paramref name="ctx"/>.
    /// Handles empty refs (0x80), hash refs (0xA0 + 32 bytes), and inline nodes (sequence).
    /// When an inline node is a leaf and the key matches, <paramref name="leafValue"/> is set.
    /// </summary>
    private static bool TryReadChildRef(
        RlpLoader rlpLoader,
        ref TreePath path,
        ReadOnlySpan<byte> parentRlp,
        ref Rlp.ValueDecoderContext ctx,
        Span<byte> remainingNibbles,
        out Hash256? nextHash,
        out int nibblesConsumed,
        out byte[]? leafValue)
    {
        nextHash = null;
        nibblesConsumed = 0;
        leafValue = null;

        if (ctx.Position >= parentRlp.Length) return false;

        byte prefix = parentRlp[ctx.Position];

        if (prefix == 0x80)
        {
            // Empty child
            return false;
        }

        if (prefix == 0xA0)
        {
            // Hash reference (0xA0 = 0x80 + 32)
            ctx.Position++;
            if (ctx.Position + 32 > parentRlp.Length) return false;

            nextHash = new Hash256(parentRlp.Slice(ctx.Position, 32));
            return true;
        }

        if (prefix >= 0xC0)
        {
            // Inline node — parse directly from parent's RLP at current position
            int inlineStart = ctx.Position;
            int inlineLen = ctx.PeekNextRlpLength();
            ReadOnlySpan<byte> inlineRlp = parentRlp.Slice(inlineStart, inlineLen);

            // Traverse the inline node without loading from cache (it has no hash)
            TraverseNode(rlpLoader, ref path, inlineRlp, remainingNibbles, out nextHash, out nibblesConsumed, out leafValue);

            // nextHash may be set if the inline node leads to a hash-referenced child
            return nextHash is not null;
        }

        // Unknown prefix — abort
        return false;
    }
}
